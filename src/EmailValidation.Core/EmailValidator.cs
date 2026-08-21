using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core;

public sealed class EmailValidator(
    IEmailNormalizer normalizer,
    IDnsMailResolver dnsResolver,
    IDomainIntelligenceEvaluator domainIntelligenceEvaluator,
    IEmailIntelligenceEvaluator emailIntelligenceEvaluator,
    IRoleAccountDetector roleDetector,
    IMailProviderDetector providerDetector,
    ISmtpMailboxProbe smtpProbe,
    IProbeSenderHealthChecker probeSenderHealthChecker,
    ICatchAllDetector catchAllDetector,
    IDomainValidationCache cache,
    IEmailClassificationEngine classifier,
    IMailProviderStrategyResolver providerStrategyResolver,
    IValidationObservationStore observationStore,
    IHistoricalSignalAggregator historicalAggregator,
    IResultEvaluator resultEvaluator,
    ISmtpSessionBudget smtpSessionBudget,
    IOptions<EmailValidationOptions> options,
    ILogger<EmailValidator> logger) : IEmailValidator, IEmailValidationExecutor
{
    private readonly EmailValidationOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _domainLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<EmailValidationResult> ValidateAsync(
        string email,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var validatedAt = DateTimeOffset.UtcNow;
        using var smtpBudget = smtpSessionBudget.Begin(_options.Smtp.MaxSmtpSessionsPerAddress);
        logger.LogInformation("Validation started");
        var normalized = normalizer.Normalize(email);
        if (!normalized.IsValid)
        {
            var reason = normalized.FailureReason ?? ReasonCode.InvalidSyntax;
            var invalidResult = InvalidSyntaxResult(
                email, reason, stopwatch.ElapsedMilliseconds, validatedAt, _options.Policy.ToVersions());
            logger.LogInformation("Validation ended with {Status} in {DurationMs} ms", invalidResult.Status, invalidResult.DurationMs);
            return invalidResult;
        }

        var localPart = normalized.LocalPart!;
        var domain = normalized.Domain!;
        var roleAccount = roleDetector.IsRoleAccount(localPart);
        var smtpRequested = request.EnableSmtp && _options.Smtp.Enabled;
        var probeSenderHealth = smtpRequested
            ? await probeSenderHealthChecker.CheckAsync(cancellationToken)
            : ProbeSenderHealth.NotChecked;
        var smtpEnabled = smtpRequested && probeSenderHealth.IsOperational;
        if (smtpRequested && !smtpEnabled)
            logger.LogWarning("Live SMTP validation disabled: {ProbeSenderHealth}", probeSenderHealth.Detail);
        var addressTask = EvaluateAddressIntelligenceAsync(
            normalized.NormalizedEmail!, localPart, domain, cancellationToken);
        var (domainData, cacheHit, catchAllProbes, domainIntelligenceDurationMs) =
            await GetDomainDataAsync(domain, smtpEnabled, cancellationToken);
        var (addressIntelligence, addressIntelligenceDurationMs) = await addressTask;
        var selectedMx = domainData.Dns.MxRecords.OrderBy(record => record.Preference).FirstOrDefault()?.Host;
        var priorObservations = await observationStore.GetDomainObservationsAsync(domain, cancellationToken);
        // Preserve old observations in storage, but only active intelligence from the
        // current published MX topology may influence a validation decision.
        var activeObservations = priorObservations
            .Where(observation => string.Equals(
                observation.TopologyFingerprint,
                domainData.Provider.TopologyFingerprint,
                StringComparison.Ordinal))
            .ToArray();
        var history = historicalAggregator.Aggregate(activeObservations);
        var activeDomainData = domainData with
        {
            Behavior = new DomainBehaviorProfile(
                domain,
                domainData.Provider.GatewayProvider,
                history.ObservationCount,
                history.TargetAcceptanceRate,
                history.RandomAcceptanceRate,
                history.RecipientRejectionRate,
                history.TemporaryFailureRate,
                history.RateLimitRate,
                history.GatewayAcceptanceRate,
                history.VerificationReliability,
                history.VerificationReliabilityLevel,
                domainData.Provider.TopologyFingerprint,
                history.GreylistingProbability)
        };

        var mailbox = new SmtpProbeResult(SmtpMailboxStatus.NotAttempted, null, null, TimeSpan.Zero, 0);
        var mxValidation = new MxValidationEvidence([], [], MxConsensus.Unknown);
        if (smtpEnabled && domainData.Dns.Status == DnsStatus.Success && selectedMx is not null)
        {
            (mailbox, mxValidation) = await ProbeMailboxAcrossMxAsync(
                domainData, normalized.NormalizedEmail!, cancellationToken);
            selectedMx = mailbox.SessionEvidence?.MxHost ?? mailbox.Evidence?.MxHost ?? selectedMx;
            logger.LogInformation("SMTP probe for {Domain} returned {Outcome}", domain, mailbox.Status);
        }

        var strategy = providerStrategyResolver.Resolve(domainData.Provider);
        var providerValidation = await strategy.EvaluateAsync(
            new ProviderValidationContext(activeDomainData, mailbox, history),
            cancellationToken);
        if (mxValidation.Consensus == MxConsensus.Conflicting)
        {
            providerValidation = providerValidation with
            {
                EffectiveCategory = SmtpResponseCategory.Unknown,
                AcceptanceStrength = AcceptanceStrength.None,
                ReasonCodes = providerValidation.ReasonCodes.Append(ReasonCode.MxResultsConflicting).Distinct().ToArray(),
                Explanation = "The consulted MX hosts returned conflicting evidence.",
                VerificationReliability = Math.Min(providerValidation.VerificationReliability, 0.25),
                VerificationReliabilityLevel = VerificationReliabilityLevel.Low
            };
        }
        var effectiveProvider = domainData.Provider with
        {
            MailboxProvider = providerValidation.MailboxProvider
        };
        var mailboxEvidence = new MailboxEvidence(domain, selectedMx ?? string.Empty, mailbox, providerValidation);

        var checks = new EmailValidationChecks
        {
            SyntaxValid = true,
            DomainExists = domainData.Dns.DomainExists,
            MxPresent = domainData.Dns.MxPresent,
            UsedImplicitMxFallback = domainData.Dns.UsedAddressFallback,
            DisposableDomain = domainData.Disposable,
            RoleAccount = roleAccount,
            CatchAll = domainData.CatchAll.Status,
            Mailbox = ToInterpretedMailboxStatus(providerValidation.EffectiveCategory)
        };
        var classificationEvidence = new EmailClassificationEvidence(
            true,
            domainData.Dns.Status,
            activeDomainData,
            roleAccount,
            mailboxEvidence,
            history)
        {
            AddressIntelligence = addressIntelligence
        };
        var classification = classifier.Classify(classificationEvidence);
        var evaluation = resultEvaluator.Evaluate(
            classification.Status,
            checks,
            activeDomainData,
            addressIntelligence,
            providerValidation,
            mailbox.Evidence,
            history);
        stopwatch.Stop();

        var result = new EmailValidationResult
        {
            Email = email,
            NormalizedEmail = normalized.NormalizedEmail,
            Status = classification.Status,
            Confidence = classification.Confidence,
            ConfidenceType = ConfidenceType.Heuristic,
            ConfidenceReason = EvidenceConfidenceExplainer.Explain(
                classification.Status, activeDomainData, mailbox, mxValidation, probeSenderHealth, providerValidation),
            Checks = checks,
            MailProvider = domainData.Provider.Provider,
            Provider = effectiveProvider,
            MxRecords = domainData.Dns.MxRecords,
            SelectedMx = selectedMx,
            ReasonCodes = classification.ReasonCodes
                .Concat(evaluation.AdditionalReasonCodes)
                .Concat(SenderHealthReasons(probeSenderHealth))
                .Distinct().ToArray(),
            UsedImplicitMxFallback = domainData.Dns.UsedAddressFallback,
            DomainIntelligence = activeDomainData,
            CatchAllEvidence = domainData.CatchAll,
            SmtpEvidence = mailbox.Evidence,
            SmtpSessionEvidence = mailbox.SessionEvidence,
            MxValidation = mxValidation,
            ProbeSenderHealth = probeSenderHealth,
            ProviderValidation = providerValidation,
            Mailbox = new MailboxValidationDetails(
                checks.Mailbox,
                providerValidation.VerificationReliability,
                providerValidation.VerificationReliabilityLevel),
            CatchAll = new CatchAllValidationDetails(
                domainData.CatchAll.Status,
                domainData.CatchAll.Confidence),
            HistoricalEvidence = history,
            ConfidenceEvidence = classification.ConfidenceEvidence ?? [],
            DetailedStatus = evaluation.DetailedStatus,
            DetailedStatuses = evaluation.DetailedStatuses,
            AddressIntelligence = addressIntelligence,
            Risk = evaluation.Risk,
            Recommendation = evaluation.Recommendation,
            Evidence = evaluation.Evidence,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Diagnostics = request.Verbose ? new ValidationDiagnostics
            {
                DomainCacheHit = cacheHit,
                SelectedMx = selectedMx,
                DnsDurationMs = (long)domainData.Dns.Duration.TotalMilliseconds,
                SmtpConnectionDurationMs = (long)mailbox.ConnectionDuration.TotalMilliseconds,
                SmtpAttempts = mxValidation.Attempts.Sum(attempt => attempt.Attempts) +
                    domainData.CatchAll.ProbeResults.Sum(attempt => attempt.Attempts),
                MxHostsAttempted = mxValidation.HostsAttempted,
                MxConsensus = mxValidation.Consensus,
                ProbeSender = mailbox.SessionEvidence?.ProbeSender ?? probeSenderHealth.Sender,
                SenderDomainHealth = probeSenderHealth.Status,
                CatchAllProbes = catchAllProbes,
                CatchAllAccepted = domainData.CatchAll.Accepted,
                CatchAllRejected = domainData.CatchAll.Rejected,
                CatchAllAmbiguous = domainData.CatchAll.Ambiguous,
                CatchAllDetail = domainData.CatchAll.Detail,
                IntelligenceLookupDurationMs = domainIntelligenceDurationMs + addressIntelligenceDurationMs,
                MailInfrastructureDurationMs = domainData.MailInfrastructure.DurationMs,
                Detail = domainData.Dns.Error ?? mailbox.Response
            } : null,
            Metadata = new ValidationResultMetadata(
                _options.Policy.ToVersions(),
                validatedAt,
                MxTopologyFingerprint: effectiveProvider.TopologyFingerprint)
        };
        var subStatus = ValidationSubStatusMapper.Map(result);
        result = result with
        {
            SubStatus = subStatus,
            SubStatuses = result.DetailedStatuses.Append(subStatus).Distinct().ToArray()
        };
        await RecordObservationsAsync(domainData, mailbox, providerValidation, selectedMx, catchAllProbes, cancellationToken);
        logger.LogInformation(
            "Validation ended with {Status}, confidence {Confidence}, in {DurationMs} ms",
            result.Status, result.Confidence, result.DurationMs);
        return result;
    }

    private async Task<(SmtpProbeResult Result, MxValidationEvidence Evidence)> ProbeMailboxAcrossMxAsync(
        DomainIntelligence domain,
        string recipient,
        CancellationToken cancellationToken)
    {
        var hosts = domain.Dns.MxRecords
            .OrderBy(record => record.Preference)
            .Select(record => record.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(_options.Smtp.MaxMxAttempts, 1, 3))
            .ToArray();
        var attempts = new List<SmtpProbeResult>(hosts.Length);
        var attemptedHosts = new List<string>(hosts.Length);

        foreach (var host in hosts)
        {
            var result = await smtpProbe.ProbeAsync(
                host, recipient, domain.Provider.Provider, cancellationToken);
            attempts.Add(result);
            attemptedHosts.Add(host);
            if (IsConclusiveMxResult(result, domain.CatchAll.Status)) break;
        }

        var consensus = CalculateMxConsensus(attempts, domain.CatchAll.Status);
        var selected = attempts.FirstOrDefault(IsStrongNegative)
            ?? attempts.FirstOrDefault(IsPositive)
            ?? attempts.Last();
        return (selected, new MxValidationEvidence(attempts, attemptedHosts, consensus));
    }

    private static bool IsConclusiveMxResult(SmtpProbeResult result, CatchAllStatus catchAll) =>
        IsStrongNegative(result) ||
        result.Status == SmtpMailboxStatus.MailboxFull ||
        (IsPositive(result) && catchAll is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll);

    private static bool IsStrongNegative(SmtpProbeResult result) =>
        result.SessionEvidence?.HasStrongRecipientRejection == true ||
        (result.SessionEvidence is null && result.Evidence?.Command == SmtpCommand.RcptTo &&
            result.Evidence.Category == SmtpResponseCategory.RecipientRejected);

    private static bool IsPositive(SmtpProbeResult result) =>
        result.Status == SmtpMailboxStatus.Accepted &&
        (result.SessionEvidence is null || result.SessionEvidence.RecipientStageReached);

    private static MxConsensus CalculateMxConsensus(
        List<SmtpProbeResult> attempts,
        CatchAllStatus catchAll)
    {
        if (attempts.Count == 0) return MxConsensus.Unknown;
        var accepted = attempts.Any(IsPositive);
        var strongPositive = accepted &&
            catchAll is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll;
        var negative = attempts.Any(IsStrongNegative);
        if (accepted && negative) return MxConsensus.Conflicting;
        if (negative) return MxConsensus.ConclusiveNegative;
        if (strongPositive) return MxConsensus.ConclusivePositive;
        return MxConsensus.ConsistentAmbiguous;
    }

    private static IEnumerable<ReasonCode> SenderHealthReasons(ProbeSenderHealth health) => health.Status switch
    {
        ProbeSenderHealthStatus.NotConfigured => [ReasonCode.ProbeSenderNotConfigured],
        ProbeSenderHealthStatus.InvalidSyntax or ProbeSenderHealthStatus.DomainNotFound or
            ProbeSenderHealthStatus.NoMailRouting or ProbeSenderHealthStatus.DnsUnavailable =>
            [ReasonCode.ProbeSenderUnhealthy],
        _ => []
    };

    private async Task<(EmailAddressIntelligence Intelligence, long DurationMs)> EvaluateAddressIntelligenceAsync(
        string email,
        string localPart,
        string domain,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var intelligence = await emailIntelligenceEvaluator.EvaluateAsync(email, localPart, domain, cancellationToken);
        watch.Stop();
        return (intelligence, watch.ElapsedMilliseconds);
    }

    private async Task<(DomainIntelligence Data, bool CacheHit, int CatchAllProbes, long IntelligenceDurationMs)> GetDomainDataAsync(
        string domain,
        bool smtpEnabled,
        CancellationToken cancellationToken)
    {
        var cached = await cache.GetAsync(domain, cancellationToken);
        if (cached is not null &&
            (!smtpEnabled || cached.CatchAll.Status != CatchAllStatus.NotAttempted || !_options.CatchAll.Enabled))
            return (cached, true, 0, 0);

        var gate = _domainLocks.GetOrAdd(domain, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            cached = await cache.GetAsync(domain, cancellationToken);
            var wasCached = cached is not null;
            var data = cached;
            var intelligenceDurationMs = 0L;
            if (data is null)
            {
                var dns = await dnsResolver.ResolveAsync(domain, cancellationToken);
                var supplemental = await domainIntelligenceEvaluator.EvaluateAsync(domain, dns, cancellationToken);
                logger.LogInformation("DNS for {Domain} returned {Status} and {MxCount} MX records", domain, dns.Status, dns.MxRecords.Count);
                data = new DomainIntelligence
                {
                    Domain = domain,
                    DomainExists = dns.DomainExists,
                    Dns = dns,
                    Disposable = supplemental.Disposable.Status is DisposableDomainStatus.KnownDisposable or DisposableDomainStatus.LikelyDisposable,
                    DisposableIntelligence = supplemental.Disposable,
                    FreeEmailProvider = supplemental.FreeEmailProvider,
                    ToxicDomain = supplemental.ToxicDomain,
                    MxForward = supplemental.MxForward,
                    DomainAge = supplemental.DomainAge,
                    MailInfrastructure = supplemental.MailInfrastructure,
                    Provider = providerDetector.DetectWithConfidence(dns.MxRecords),
                    CatchAll = new CatchAllDetectionResult(CatchAllStatus.NotAttempted, 0, 0, 0, 0),
                    ObservedAt = DateTimeOffset.UtcNow,
                    StrategyVersion = _options.Policy.ProviderStrategyVersion
                };
                intelligenceDurationMs = supplemental.LookupDurationMs;
            }

            var probes = 0;
            var selectedMx = data.Dns.MxRecords.OrderBy(record => record.Preference).FirstOrDefault()?.Host;
            if (smtpEnabled && _options.CatchAll.Enabled && data.CatchAll.Status == CatchAllStatus.NotAttempted && selectedMx is not null)
            {
                var detection = await catchAllDetector.DetectAsync(domain, selectedMx, data.Provider.Provider, cancellationToken);
                data = data with
                {
                    CatchAll = detection,
                    ObservedAt = DateTimeOffset.UtcNow
                };
                probes = detection.Probes;
                logger.LogInformation("Catch-all probe for {Domain} returned {Outcome}", domain, detection.Status);
            }

            var lifetime = TimeSpan.FromMinutes(data.CatchAll.Status == CatchAllStatus.NotAttempted
                ? _options.Dns.CacheMinutes
                : Math.Min(_options.Dns.CacheMinutes, _options.CatchAll.CacheMinutes));
            data = data with { EvidenceExpiresAt = DateTimeOffset.UtcNow.Add(lifetime) };
            await cache.StoreAsync(data, lifetime, cancellationToken);
            return (data, wasCached, probes, intelligenceDurationMs);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RecordObservationsAsync(
        DomainIntelligence domain,
        SmtpProbeResult mailbox,
        ProviderValidationResult providerValidation,
        string? selectedMx,
        int catchAllProbes,
        CancellationToken cancellationToken)
    {
        if (catchAllProbes > 0)
        {
            var catchAllCategory = domain.CatchAll.Status switch
            {
                CatchAllStatus.LikelyCatchAll => SmtpResponseCategory.Accepted,
                CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll => SmtpResponseCategory.RecipientRejected,
                _ => SmtpResponseCategory.Unknown
            };
            await observationStore.RecordAsync(new ValidationObservation(
                domain.Domain,
                ValidationObservationType.CatchAllProbe,
                domain.Provider.Provider,
                selectedMx,
                domain.CatchAll.Status,
                domain.CatchAll.Confidence,
                catchAllCategory,
                DateTimeOffset.UtcNow,
                0,
                domain.CatchAll.Accepted,
                domain.CatchAll.Probes,
                domain.CatchAll.Rejected,
                domain.Provider.GatewayProvider,
                domain.Provider.TopologyFingerprint), cancellationToken);
        }

        if (mailbox.Status != SmtpMailboxStatus.NotAttempted)
        {
            await observationStore.RecordAsync(new ValidationObservation(
                domain.Domain,
                ValidationObservationType.MailboxProbe,
                domain.Provider.Provider,
                selectedMx,
                domain.CatchAll.Status,
                domain.CatchAll.Confidence,
                providerValidation.EffectiveCategory,
                DateTimeOffset.UtcNow,
                mailbox.Evidence?.ElapsedMilliseconds ?? (long)mailbox.ConnectionDuration.TotalMilliseconds,
                GatewayProvider: domain.Provider.GatewayProvider,
                TopologyFingerprint: domain.Provider.TopologyFingerprint), cancellationToken);
        }
    }

    private static SmtpMailboxStatus ToInterpretedMailboxStatus(SmtpResponseCategory category) => category switch
    {
        SmtpResponseCategory.Accepted => SmtpMailboxStatus.Accepted,
        SmtpResponseCategory.RecipientRejected => SmtpMailboxStatus.Rejected,
        SmtpResponseCategory.MailboxFull => SmtpMailboxStatus.MailboxFull,
        SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted or SmtpResponseCategory.RateLimited =>
            SmtpMailboxStatus.TemporaryFailure,
        SmtpResponseCategory.VerificationBlocked => SmtpMailboxStatus.Blocked,
        SmtpResponseCategory.ConnectionRejected => SmtpMailboxStatus.ConnectionFailure,
        SmtpResponseCategory.Timeout => SmtpMailboxStatus.Timeout,
        SmtpResponseCategory.NotAttempted => SmtpMailboxStatus.NotAttempted,
        _ => SmtpMailboxStatus.Unknown
    };

    private static EmailValidationResult InvalidSyntaxResult(
        string email,
        ReasonCode reason,
        long durationMs,
        DateTimeOffset validatedAt,
        ValidationPolicyVersions policy) => new()
        {
            Email = email,
            Status = EmailValidationStatus.Invalid,
            Confidence = 0.99,
            ConfidenceType = ConfidenceType.Heuristic,
            ConfidenceReason = "High confidence because the address failed deterministic syntax validation.",
            Checks = new EmailValidationChecks
            {
                Mailbox = SmtpMailboxStatus.NotAttempted,
                CatchAll = CatchAllStatus.NotAttempted
            },
            ReasonCodes = [reason],
            DetailedStatus = DetailedStatus.InvalidSyntax,
            DetailedStatuses = [DetailedStatus.InvalidSyntax],
            SubStatus = DetailedStatus.InvalidSyntax,
            SubStatuses = [DetailedStatus.InvalidSyntax],
            Risk = new ValidationRisk(BounceRisk.High, false, SpamTrapRiskStatus.Unknown, AbuseRiskStatus.Unknown),
            Recommendation = new SendRecommendation(false, RecommendationRisk.High, ["TechnicallyInvalid"]),
            Evidence = [new EvidenceProvenance("Syntax", EvidenceSource.LocalIntelligence, 0.99, "The address failed syntax validation.")],
            DurationMs = durationMs,
            Metadata = new ValidationResultMetadata(policy, validatedAt)
        };
}
