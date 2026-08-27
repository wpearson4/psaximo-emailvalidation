using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core;

public sealed class EmailValidator(
    IEmailNormalizer normalizer,
    IEmailIntelligenceEvaluator emailIntelligenceEvaluator,
    IRoleAccountDetector roleDetector,
    ISmtpMailboxProbe smtpProbe,
    IProbeSenderHealthChecker probeSenderHealthChecker,
    IEmailClassificationEngine classifier,
    IMailProviderStrategyResolver providerStrategyResolver,
    IValidationObservationStore observationStore,
    IHistoricalSignalAggregator historicalAggregator,
    IResultEvaluator resultEvaluator,
    ISmtpSessionBudget smtpSessionBudget,
    IValidationPersistenceMetrics persistenceMetrics,
    IDomainIntelligenceService domainIntelligenceService,
    ISmtpProviderDetector smtpProviderDetector,
    IOptions<EmailValidationOptions> options,
    ILogger<EmailValidator> logger,
    IValidationProgressReporter? progressReporter = null) : IEmailValidator, IEmailValidationExecutor
{
    private readonly EmailValidationOptions _options = options.Value;

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
            if (request.EnableSmtp) persistenceMetrics.RecordSmtpValidationAvoided();
            var reason = normalized.FailureReason ?? ReasonCode.InvalidSyntax;
            var invalidResult = InvalidSyntaxResult(
                email, reason, stopwatch.ElapsedMilliseconds, validatedAt, _options.Policy.ToVersions());
            logger.LogInformation("Validation ended with {Status} in {DurationMs} ms", invalidResult.Status, invalidResult.DurationMs);
            return invalidResult;
        }

        var localPart = normalized.LocalPart!;
        var domain = normalized.Domain!;
        var normalizedAddress = new NormalizedEmailAddress(normalized.NormalizedEmail!, localPart, domain);
        var roleDetection = roleDetector.Detect(normalizedAddress);
        var roleAccount = roleDetection.IsRoleAddress;
        var smtpRequested = request.EnableSmtp && _options.Smtp.Enabled;
        var probeSenderHealth = smtpRequested
            ? await probeSenderHealthChecker.CheckAsync(cancellationToken)
            : ProbeSenderHealth.NotChecked;
        var smtpEnabled = smtpRequested && probeSenderHealth.IsOperational;
        if (smtpRequested && !smtpEnabled)
            logger.LogWarning("Live SMTP validation disabled: {ProbeSenderHealth}", probeSenderHealth.Detail);
        var addressTask = EvaluateAddressIntelligenceAsync(
            normalized.NormalizedEmail!, localPart, domain, cancellationToken);
        var (domainData, cacheHit, catchAllProbes, domainIntelligenceDurationMs, validationPlan) =
            await GetDomainDataAsync(domain, smtpEnabled, cancellationToken);
        await ReportProgressAsync(request.ValidationId, ValidationProgressStage.DomainChecks,
            "Domain and MX validation completed.", cancellationToken).ConfigureAwait(false);
        var (addressIntelligence, addressIntelligenceDurationMs) = await addressTask;
        addressIntelligence = addressIntelligence with { RoleAddress = roleDetection };
        var selectedMx = domainData.Dns.MxRecords.OrderBy(record => record.Preference).FirstOrDefault()?.Host;
        await ReportProgressAsync(request.ValidationId, ValidationProgressStage.ProviderChecks,
            $"Provider identified as {domainData.Provider.Provider}.", cancellationToken).ConfigureAwait(false);
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

        var mailbox = new SmtpProbeResult(SmtpMailboxStatus.NotAttempted, null, null, TimeSpan.Zero, 0)
        {
            Disposition = SmtpProbeDisposition.NotAttempted
        };
        var mxValidation = new MxValidationEvidence([], [], MxConsensus.Unknown);
        if (validationPlan.PerformMailboxProbe && domainData.Dns.Status == DnsStatus.Success && selectedMx is not null)
        {
            await ReportProgressAsync(request.ValidationId, ValidationProgressStage.SmtpValidation,
                "Mailbox SMTP validation started.", cancellationToken).ConfigureAwait(false);
            (mailbox, mxValidation) = await ProbeMailboxAcrossMxAsync(
                domainData, normalized.NormalizedEmail!, cancellationToken);
            selectedMx = mailbox.SessionEvidence?.MxHost ?? mailbox.Evidence?.MxHost ?? selectedMx;
            logger.LogInformation("SMTP probe for {Domain} returned {Outcome}", domain, mailbox.Status);
        }
        else if (validationPlan.UsePersistedCatchAll)
        {
            await ReportProgressAsync(request.ValidationId, ValidationProgressStage.PersistedIntelligence,
                "Using persisted domain intelligence; mailbox SMTP validation was not required.", cancellationToken)
                .ConfigureAwait(false);
            persistenceMetrics.RecordCatchAllReuse(
                catchAllProbeAvoided: smtpEnabled && _options.CatchAll.Enabled,
                mailboxProbeAvoided: smtpEnabled);
            logger.LogDebug(
                "Catch-all intelligence reused for {Domain}; randomized-recipient and mailbox SMTP probes skipped",
                domain);
        }
        if (smtpRequested)
        {
            if (mailbox.ProbeAttempted) persistenceMetrics.RecordSmtpValidationPerformed();
            else persistenceMetrics.RecordSmtpValidationAvoided();
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
        var smtpProvider = mailbox.SessionEvidence is not null
            ? smtpProviderDetector.Detect(mailbox.SessionEvidence)
            : null;
        var effectiveProvider = domainData.Provider with
        {
            MailboxProvider = providerValidation.MailboxProvider,
            SmtpObservedProvider = smtpProvider?.SmtpObservedProvider ?? MailProvider.Unknown,
            SmtpEvidenceConfidence = smtpProvider?.SmtpEvidenceConfidence ?? 0,
            Evidence = (domainData.Provider.Evidence ?? [])
                .Concat(smtpProvider?.Evidence ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
        var effectiveDomainData = activeDomainData with { Provider = effectiveProvider };
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
            ProbeAttempted = mailbox.ProbeAttempted,
            ProbeDisposition = mailbox.Disposition,
            RetryAfter = mailbox.RetryAfter,
            RequiresSmtpUtf8 = normalized.RequiresSmtpUtf8,
            SmtpUtf8Supported = mailbox.SessionEvidence is null
                ? null
                : mailbox.SessionEvidence.SmtpUtf8Advertised,
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
            DomainIntelligence = effectiveDomainData,
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
            DeliverabilityRisk = CreateDeliverabilityRisk(roleDetection, domainData, addressIntelligence),
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
                UsedPersistedCatchAll = validationPlan.UsePersistedCatchAll,
                MailboxProbeSkippedDueToCatchAll = validationPlan.UsePersistedCatchAll,
                CatchAllObservedAt = domainData.CatchAll.ObservedAt ?? domainData.ObservedAt,
                IntelligenceLookupDurationMs = domainIntelligenceDurationMs + addressIntelligenceDurationMs,
                MailInfrastructureDurationMs = domainData.MailInfrastructure.DurationMs,
                ProbeAttempted = mailbox.ProbeAttempted,
                ProbeDisposition = mailbox.Disposition,
                SmtpResponseCategory = providerValidation.EffectiveCategory,
                RetryAfter = mailbox.RetryAfter,
                Detail = domainData.Dns.Error ?? mailbox.Response
            } : null,
            Metadata = new ValidationResultMetadata(
                _options.Policy.ToVersions(),
                validatedAt,
                MxTopologyFingerprint: effectiveProvider.TopologyFingerprint,
                ResultSource: validationPlan.UsePersistedCatchAll
                    ? ValidationResultSource.PersistentDomainIntelligence
                    : ValidationResultSource.LiveValidation)
        };
        var evidenceQuality = ValidationEvidenceAssessment.Quality(
            result.Status, activeDomainData, mailbox, providerValidation);
        var catchAllClassification = ValidationEvidenceAssessment.CatchAllType(
            result.Status, activeDomainData, providerValidation, history);
        var subStatus = catchAllClassification switch
        {
            CatchAllClassification.Confirmed => DetailedStatus.CatchAllConfirmed,
            CatchAllClassification.GatewayAmbiguous => DetailedStatus.CatchAllGatewayAmbiguous,
            CatchAllClassification.Historical => DetailedStatus.CatchAllHistorical,
            _ => ValidationSubStatusMapper.Map(result)
        };
        result = result with
        {
            EvidenceQuality = evidenceQuality,
            CatchAllClassification = catchAllClassification,
            SubStatus = subStatus,
            SubStatuses = result.DetailedStatuses.Append(subStatus).Distinct().ToArray()
        };
        result = result with { UnknownContext = UnknownValidationContextBuilder.Build(result) };
        persistenceMetrics.RecordSmtpUtf8(
            result.RequiresSmtpUtf8,
            result.SmtpUtf8Supported is not false);
        await RecordObservationsAsync(domainData, mailbox, providerValidation, selectedMx, catchAllProbes, cancellationToken);
        logger.LogInformation(
            "Validation ended with {Status}, confidence {Confidence}, in {DurationMs} ms",
            result.Status, result.Confidence, result.DurationMs);
        return result;
    }

    private async Task ReportProgressAsync(
        string? validationId,
        ValidationProgressStage stage,
        string message,
        CancellationToken cancellationToken)
    {
        if (progressReporter is null || string.IsNullOrWhiteSpace(validationId)) return;
        try
        {
            await progressReporter.ReportAsync(validationId, stage, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Validation progress {Stage} could not be reported for {ValidationId}", stage, validationId);
        }
    }

    private async Task<(SmtpProbeResult Result, MxValidationEvidence Evidence)> ProbeMailboxAcrossMxAsync(
        DomainIntelligence domain,
        string recipient,
        CancellationToken cancellationToken)
    {
        var hosts = domain.Dns.MxRecords
            .OrderBy(record => record.Preference)
            .ThenBy(record => record.Host, StringComparer.OrdinalIgnoreCase)
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

    private async Task<(DomainIntelligence Data, bool CacheHit, int CatchAllProbes, long IntelligenceDurationMs, ValidationPlan Plan)> GetDomainDataAsync(
        string domain,
        bool smtpEnabled,
        CancellationToken cancellationToken)
    {
        var acquisition = await domainIntelligenceService.AcquireAsync(domain, smtpEnabled, cancellationToken)
            .ConfigureAwait(false);
        return (
            acquisition.Intelligence,
            acquisition.Source is not DomainIntelligenceSource.LiveAnalysis,
            acquisition.CatchAllProbes,
            acquisition.AnalysisDurationMs,
            acquisition.Plan);
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
            var catchAllCategory = domain.CatchAll.RefreshInconclusive
                ? SmtpResponseCategory.Unknown
                : domain.CatchAll.Status switch
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
                domain.CatchAll.RefreshInconclusive ? 0 : domain.CatchAll.Accepted,
                domain.CatchAll.RefreshInconclusive ? catchAllProbes : domain.CatchAll.Probes,
                domain.CatchAll.RefreshInconclusive ? 0 : domain.CatchAll.Rejected,
                domain.Provider.GatewayProvider,
                domain.Provider.TopologyFingerprint), cancellationToken);
        }

        if (mailbox.Status != SmtpMailboxStatus.NotAttempted || mailbox.Evidence?.Reputation is not null)
        {
            await observationStore.RecordAsync(new ValidationObservation(
                domain.Domain,
                mailbox.Status == SmtpMailboxStatus.NotAttempted
                    ? ValidationObservationType.ReputationDecision
                    : ValidationObservationType.MailboxProbe,
                domain.Provider.Provider,
                selectedMx,
                domain.CatchAll.Status,
                domain.CatchAll.Confidence,
                providerValidation.EffectiveCategory,
                DateTimeOffset.UtcNow,
                mailbox.Evidence?.ElapsedMilliseconds ?? (long)mailbox.ConnectionDuration.TotalMilliseconds,
                GatewayProvider: domain.Provider.GatewayProvider,
                TopologyFingerprint: domain.Provider.TopologyFingerprint,
                Reputation: mailbox.Evidence?.Reputation), cancellationToken);
        }
    }

    private static SmtpMailboxStatus ToInterpretedMailboxStatus(SmtpResponseCategory category) => category switch
    {
        SmtpResponseCategory.Accepted => SmtpMailboxStatus.Accepted,
        SmtpResponseCategory.RecipientRejected => SmtpMailboxStatus.Rejected,
        SmtpResponseCategory.MailboxFull => SmtpMailboxStatus.MailboxFull,
        SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted or SmtpResponseCategory.RateLimited =>
            SmtpMailboxStatus.TemporaryFailure,
        SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.SmtpUtf8Unsupported => SmtpMailboxStatus.Blocked,
        SmtpResponseCategory.LocalCooldown => SmtpMailboxStatus.NotAttempted,
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
            EvidenceQuality = EvidenceQuality.Conclusive,
            ProbeDisposition = SmtpProbeDisposition.NotAttempted,
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

    private static DeliverabilityRisk CreateDeliverabilityRisk(
        RoleAddressDetectionResult role,
        DomainIntelligence domain,
        EmailAddressIntelligence address)
    {
        var spamTrap = address.SpamTrapRisk.Status switch
        {
            SpamTrapRiskStatus.KnownSpamTrap when address.SpamTrapRisk.EvidenceSource is not EvidenceSource.Heuristic =>
                new SpamTrapRiskAssessment(
                    SpamTrapRiskLevel.Known,
                    SpamTrapEvidenceKind.TrustedDatasetMatch,
                    address.SpamTrapRisk.Confidence,
                    address.SpamTrapRisk.EvidenceSource?.ToString()),
            SpamTrapRiskStatus.LikelySpamTrap => new SpamTrapRiskAssessment(
                SpamTrapRiskLevel.High,
                address.SpamTrapRisk.EvidenceSource == EvidenceSource.Heuristic
                    ? SpamTrapEvidenceKind.HeuristicOnly
                    : SpamTrapEvidenceKind.DomainRiskPattern,
                address.SpamTrapRisk.Confidence,
                address.SpamTrapRisk.EvidenceSource?.ToString()),
            SpamTrapRiskStatus.PossibleSpamTrap => new SpamTrapRiskAssessment(
                SpamTrapRiskLevel.Elevated,
                SpamTrapEvidenceKind.HeuristicOnly,
                address.SpamTrapRisk.Confidence,
                address.SpamTrapRisk.EvidenceSource?.ToString()),
            _ => SpamTrapRiskAssessment.None
        };
        var reasons = new List<MailingRiskReason>();
        if (role.IsRoleAddress) reasons.Add(MailingRiskReason.RoleAccount);
        if (domain.Disposable) reasons.Add(MailingRiskReason.DisposableAddress);
        if (spamTrap.Level is SpamTrapRiskLevel.Elevated or SpamTrapRiskLevel.High or SpamTrapRiskLevel.Known)
            reasons.Add(MailingRiskReason.SpamTrapIndicator);
        if (address.Suppression.Status == SuppressionStatus.Suppressed)
            reasons.Add(MailingRiskReason.KnownSuppression);
        if (address.AbuseRisk.Status == AbuseRiskStatus.KnownRisk)
            reasons.Add(MailingRiskReason.KnownAbuse);
        if (domain.ToxicDomain.Status is ToxicDomainStatus.LikelyToxic or ToxicDomainStatus.KnownToxic)
            reasons.Add(MailingRiskReason.ToxicDomain);
        return new DeliverabilityRisk(
            role,
            domain.DisposableIntelligence,
            spamTrap,
            address.Suppression.Status == SuppressionStatus.Suppressed ? DeliverabilityRiskLevel.High : null,
            address.AbuseRisk.Status == AbuseRiskStatus.KnownRisk ? DeliverabilityRiskLevel.High : null,
            domain.ToxicDomain.Status switch
            {
                ToxicDomainStatus.KnownToxic => DeliverabilityRiskLevel.High,
                ToxicDomainStatus.LikelyToxic => DeliverabilityRiskLevel.Medium,
                _ => null
            },
            reasons,
            new[]
            {
                role.IsRoleAddress ? 0.99 : 0,
                domain.DisposableIntelligence.Confidence,
                address.SpamTrapRisk.Confidence
            }.Max());
    }
}
