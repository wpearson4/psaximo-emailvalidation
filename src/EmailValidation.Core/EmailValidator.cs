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
        var observationSessionId = Guid.NewGuid().ToString("N");
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

        var smtpProvider = mailbox.SessionEvidence is not null
            ? smtpProviderDetector.Detect(mailbox.SessionEvidence)
            : null;
        var reconciledProvider = ReconcileProvider(domainData.Provider, smtpProvider);
        var observationProvider = reconciledProvider.Provider;
        var targetAcceptanceUncontested = mxValidation.Attempts.Count > 0 &&
            mxValidation.Attempts.All(IsPositive);
        var evaluatedCatchAll = DomainRecipientBehaviorPolicy.Evaluate(
            domainData.CatchAll,
            mailbox,
            activeObservations,
            observationProvider,
            _options.CatchAll,
            catchAllProbes > 0,
            mxValidation.Consensus,
            targetAcceptanceUncontested);
        if (evaluatedCatchAll != domainData.CatchAll)
        {
            activeDomainData = activeDomainData with { CatchAll = evaluatedCatchAll };
            await domainIntelligenceService.UpdateRecipientBehaviorAsync(activeDomainData, cancellationToken)
                .ConfigureAwait(false);
        }

        var strategyDomainData = activeDomainData with { Provider = reconciledProvider };
        var strategy = providerStrategyResolver.Resolve(reconciledProvider);
        var providerValidation = await strategy.EvaluateAsync(
            new ProviderValidationContext(strategyDomainData, mailbox, history),
            cancellationToken);
        if (reconciledProvider.Evidence?.Contains("ProviderEvidenceConflict", StringComparer.Ordinal) == true)
        {
            var directRecipientOutcome =
                SmtpRecipientEvidencePolicy.HasStrongRecipientRejection(mailbox) ||
                SmtpRecipientEvidencePolicy.HasRecipientMailboxFull(mailbox);
            providerValidation = directRecipientOutcome
                ? providerValidation with
                {
                    ReasonCodes = providerValidation.ReasonCodes
                        .Where(reason => reason != ReasonCode.ProviderDetected)
                        .Append(ReasonCode.ProviderEvidenceConflicting)
                        .Distinct()
                        .ToArray(),
                    Explanation = $"Provider identity is conflicting. {providerValidation.Explanation}"
                }
                : providerValidation with
                {
                    EffectiveCategory = SmtpResponseCategory.Unknown,
                    AcceptanceStrength = AcceptanceStrength.None,
                    ReasonCodes = providerValidation.ReasonCodes
                        .Where(reason => reason != ReasonCode.ProviderDetected &&
                            !IsMutuallyExclusiveMxOutcome(reason))
                        .Append(ReasonCode.ProviderEvidenceConflicting)
                        .Distinct()
                        .ToArray(),
                    Explanation = "Published MX identity and SMTP-observed provider identity conflict.",
                    VerificationReliability = Math.Min(providerValidation.VerificationReliability, 0.20),
                    VerificationReliabilityLevel = VerificationReliabilityLevel.Low
                };
        }
        if (mxValidation.Consensus == MxConsensus.Conflicting)
        {
            providerValidation = providerValidation with
            {
                EffectiveCategory = SmtpResponseCategory.Unknown,
                AcceptanceStrength = AcceptanceStrength.None,
                ReasonCodes = providerValidation.ReasonCodes
                    .Where(reason => !IsMutuallyExclusiveMxOutcome(reason))
                    .Append(ReasonCode.MxResultsConflicting)
                    .Distinct()
                    .ToArray(),
                Explanation = "The consulted MX hosts returned conflicting evidence.",
                VerificationReliability = Math.Min(providerValidation.VerificationReliability, 0.25),
                VerificationReliabilityLevel = VerificationReliabilityLevel.Low
            };
        }
        var effectiveProvider = reconciledProvider with
        {
            GatewayProvider = providerValidation.GatewayProvider != GatewayProvider.Unknown
                ? providerValidation.GatewayProvider
                : reconciledProvider.GatewayProvider,
            MailboxProvider = providerValidation.MailboxProvider,
            SmtpObservedProvider = smtpProvider?.SmtpObservedProvider ?? MailProvider.Unknown,
            SmtpEvidenceConfidence = smtpProvider?.SmtpEvidenceConfidence ?? 0,
            Evidence = reconciledProvider.Evidence ?? []
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
            CatchAll = activeDomainData.CatchAll.Status,
            Mailbox = ToInterpretedMailboxStatus(providerValidation.EffectiveCategory)
        };
        var classificationEvidence = new EmailClassificationEvidence(
            true,
            domainData.Dns.Status,
            effectiveDomainData,
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
            effectiveDomainData,
            addressIntelligence,
            providerValidation,
            mailbox.Evidence,
            history);
        var resultRetryAfter = mailbox.RetryAfter;
        if (activeDomainData.CatchAll.ReasonCode == CatchAllReasonCode.AcceptAllCandidate &&
            activeDomainData.CatchAll.ObservedAt is { } candidateObservedAt)
        {
            var confirmationAt = candidateObservedAt.AddMinutes(Math.Max(
                1,
                _options.CatchAll.AcceptAllMinimumObservationSeparationMinutes));
            if (resultRetryAfter is null || resultRetryAfter < confirmationAt)
                resultRetryAfter = confirmationAt;
        }
        stopwatch.Stop();

        var result = new EmailValidationResult
        {
            Email = email,
            NormalizedEmail = normalized.NormalizedEmail,
            Status = classification.Status,
            Confidence = classification.Confidence,
            ConfidenceType = ConfidenceType.Heuristic,
            ConfidenceReason = EvidenceConfidenceExplainer.Explain(
                classification.Status, effectiveDomainData, mailbox, mxValidation, probeSenderHealth, providerValidation),
            ProbeAttempted = mailbox.ProbeAttempted,
            ProbeDisposition = mailbox.Disposition,
            RetryAfter = resultRetryAfter,
            RequiresSmtpUtf8 = normalized.RequiresSmtpUtf8,
            SmtpUtf8Supported = mailbox.SessionEvidence is null
                ? null
                : mailbox.SessionEvidence.SmtpUtf8Advertised,
            Checks = checks,
            MailProvider = effectiveProvider.Provider,
            Provider = effectiveProvider,
            MxRecords = domainData.Dns.MxRecords,
            SelectedMx = selectedMx,
            ReasonCodes = classification.ReasonCodes
                .Concat(evaluation.AdditionalReasonCodes)
                .Concat(SenderHealthReasons(probeSenderHealth))
                .Distinct().ToArray(),
            UsedImplicitMxFallback = domainData.Dns.UsedAddressFallback,
            // SMTP banner reconciliation is evidence for this mailbox exchange, not
            // a replacement for the DNS-derived domain provider persisted by the
            // intelligence layer. Keep the effective provider on the result itself.
            DomainIntelligence = activeDomainData,
            CatchAllEvidence = activeDomainData.CatchAll,
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
                activeDomainData.CatchAll.Status,
                activeDomainData.CatchAll.Confidence),
            HistoricalEvidence = history,
            ConfidenceEvidence = classification.ConfidenceEvidence ?? [],
            DetailedStatus = evaluation.DetailedStatus,
            DetailedStatuses = evaluation.DetailedStatuses,
            AddressIntelligence = addressIntelligence,
            Risk = evaluation.Risk,
            DeliverabilityRisk = CreateDeliverabilityRisk(roleDetection, activeDomainData, addressIntelligence),
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
                CatchAllAccepted = activeDomainData.CatchAll.Accepted,
                CatchAllRejected = activeDomainData.CatchAll.Rejected,
                CatchAllAmbiguous = activeDomainData.CatchAll.Ambiguous,
                CatchAllDetail = activeDomainData.CatchAll.Detail,
                UsedPersistedCatchAll = validationPlan.UsePersistedCatchAll,
                MailboxProbeSkippedDueToCatchAll = validationPlan.UsePersistedCatchAll,
                CatchAllObservedAt = activeDomainData.CatchAll.ObservedAt ?? activeDomainData.ObservedAt,
                IntelligenceLookupDurationMs = domainIntelligenceDurationMs + addressIntelligenceDurationMs,
                MailInfrastructureDurationMs = domainData.MailInfrastructure.DurationMs,
                ProbeAttempted = mailbox.ProbeAttempted,
                ProbeDisposition = mailbox.Disposition,
                SmtpResponseCategory = providerValidation.EffectiveCategory,
                RetryAfter = resultRetryAfter,
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
            result.Status, effectiveDomainData, mailbox, providerValidation);
        var catchAllClassification = ValidationEvidenceAssessment.CatchAllType(
            result.Status, effectiveDomainData, providerValidation, history);
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
        await RecordObservationsAsync(
            effectiveDomainData,
            mailbox,
            providerValidation,
            selectedMx,
            catchAllProbes,
            observationProvider,
            observationSessionId,
            targetAcceptanceUncontested,
            mxValidation.Consensus == MxConsensus.Conflicting,
            cancellationToken);
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
        var endpoints = domain.Dns.MxRecords
            .OrderBy(record => record.Preference)
            .ThenBy(record => record.Host, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(record => record.Host, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(_options.Smtp.MaxMxAttempts, 1, 3))
            .ToArray();
        var attempts = new List<SmtpProbeResult>(endpoints.Length);
        var attemptedHosts = new List<string>(endpoints.Length);
        var preferenceGroupStart = 0;

        for (var index = 0; index < endpoints.Length; index++)
        {
            var endpoint = endpoints[index];
            var result = await smtpProbe.ProbeAsync(
                endpoint.Host, recipient, domain.Provider.Provider, cancellationToken);
            attempts.Add(result);
            attemptedHosts.Add(endpoint.Host);

            var endOfPreferenceGroup = index == endpoints.Length - 1 ||
                endpoints[index + 1].Preference != endpoint.Preference;
            if (!endOfPreferenceGroup) continue;
            if (attempts.Skip(preferenceGroupStart)
                .Any(attempt => IsConclusiveMxResult(attempt, domain.CatchAll.Status)))
                break;
            preferenceGroupStart = attempts.Count;
        }

        var consensus = CalculateMxConsensus(attempts, domain.CatchAll.Status);
        var selected = attempts.FirstOrDefault(IsStrongNegative)
            ?? attempts.FirstOrDefault(IsMailboxFull)
            ?? attempts.FirstOrDefault(IsPositive)
            // A later MX can be skipped after the first live attempt activates local
            // pacing. Preserve the actual SMTP evidence instead of replacing it with
            // a control-path deferral that did not contact the destination.
            ?? attempts.LastOrDefault(result => result.ProbeAttempted)
            ?? attempts.Last();
        return (selected, new MxValidationEvidence(attempts, attemptedHosts, consensus));
    }

    private static bool IsConclusiveMxResult(SmtpProbeResult result, CatchAllStatus catchAll) =>
        IsStrongNegative(result) ||
        IsMailboxFull(result) ||
        (IsPositive(result) && catchAll is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll);

    private static bool IsStrongNegative(SmtpProbeResult result) =>
        SmtpRecipientEvidencePolicy.HasStrongRecipientRejection(result);

    private static bool IsPositive(SmtpProbeResult result) =>
        SmtpRecipientEvidencePolicy.HasRecipientAcceptance(result);

    private static bool IsMailboxFull(SmtpProbeResult result) =>
        SmtpRecipientEvidencePolicy.HasRecipientMailboxFull(result);

    private static bool IsMutuallyExclusiveMxOutcome(ReasonCode reason) => reason is
        ReasonCode.MailboxRejected or
        ReasonCode.MicrosoftRecipientRejected or
        ReasonCode.MailboxAccepted or
        ReasonCode.GatewayAccepted or
        ReasonCode.MailboxAcceptanceAmbiguous;

    private static ProviderDetectionResult ReconcileProvider(
        ProviderDetectionResult dnsProvider,
        ProviderDetectionResult? smtpProvider)
    {
        var observed = smtpProvider?.SmtpObservedProvider ?? MailProvider.Unknown;
        if (observed == MailProvider.Unknown) return dnsProvider;

        var combinedEvidence = (dnsProvider.Evidence ?? [])
            .Concat(smtpProvider?.Evidence ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var smtpConfidence = smtpProvider?.SmtpEvidenceConfidence ?? smtpProvider?.Confidence ?? 0;
        if (dnsProvider.Provider is MailProvider.Unknown or MailProvider.GenericSmtp)
            return dnsProvider with
            {
                Provider = observed,
                Confidence = smtpConfidence,
                MatchedSignature = smtpProvider?.MatchedSignature,
                Evidence = combinedEvidence,
                SmtpObservedProvider = observed,
                SmtpEvidenceConfidence = smtpConfidence
            };
        if (AreProviderStrategiesCompatible(dnsProvider.Provider, observed))
            return dnsProvider with
            {
                Confidence = Math.Max(dnsProvider.Confidence, smtpConfidence),
                Evidence = combinedEvidence,
                SmtpObservedProvider = observed,
                SmtpEvidenceConfidence = smtpConfidence
            };

        return dnsProvider with
        {
            Provider = MailProvider.Unknown,
            Confidence = Math.Min(dnsProvider.Confidence, smtpConfidence),
            MatchedSignature = "Conflicting MX and SMTP provider evidence",
            Evidence = combinedEvidence.Append("ProviderEvidenceConflict").Distinct(StringComparer.Ordinal).ToArray(),
            SmtpObservedProvider = observed,
            SmtpEvidenceConfidence = smtpConfidence
        };
    }

    private static bool AreProviderStrategiesCompatible(MailProvider left, MailProvider right) =>
        left == right ||
        (left is MailProvider.Microsoft365 or MailProvider.MicrosoftConsumer &&
         right is MailProvider.Microsoft365 or MailProvider.MicrosoftConsumer);

    private static MxConsensus CalculateMxConsensus(
        List<SmtpProbeResult> attempts,
        CatchAllStatus catchAll)
    {
        if (attempts.Count == 0) return MxConsensus.Unknown;
        var accepted = attempts.Any(IsPositive);
        var mailboxFull = attempts.Any(IsMailboxFull);
        var strongPositive = accepted &&
            catchAll is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll;
        var negative = attempts.Any(IsStrongNegative);
        if ((accepted || mailboxFull) && negative) return MxConsensus.Conflicting;
        if (negative) return MxConsensus.ConclusiveNegative;
        if (mailboxFull || strongPositive) return MxConsensus.ConclusivePositive;
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
        MailProvider observationProvider,
        string observationSessionId,
        bool targetAcceptanceUncontested,
        bool targetEvidenceContested,
        CancellationToken cancellationToken)
    {
        var targetObservedAt = SmtpRecipientEvidencePolicy.RecipientObservedAt(mailbox);
        var targetStrongRejection = SmtpRecipientEvidencePolicy.HasStrongRecipientRejection(mailbox);
        var targetRecipientQualified = targetObservedAt is not null &&
            (targetStrongRejection ||
             targetAcceptanceUncontested && SmtpRecipientEvidencePolicy.HasRecipientAcceptance(mailbox));
        var observedRecipientCategory = targetStrongRejection
            ? SmtpResponseCategory.RecipientRejected
            : providerValidation.EffectiveCategory;
        var targetMx = SmtpRecipientEvidencePolicy.MxHost(mailbox);
        if (catchAllProbes > 0)
        {
            var controlResults = domain.CatchAll.ProbeResults
                .TakeLast(Math.Min(catchAllProbes, domain.CatchAll.ProbeResults.Count))
                .ToArray();
            var accepted = controlResults.Count(SmtpRecipientEvidencePolicy.HasRecipientAcceptance);
            var rejected = controlResults.Count(SmtpRecipientEvidencePolicy.HasStrongRecipientRejection);
            var catchAllCategory = controlResults.Length > 0 && accepted == controlResults.Length
                ? SmtpResponseCategory.Accepted
                : controlResults.Length > 0 && rejected == controlResults.Length
                    ? SmtpResponseCategory.RecipientRejected
                    : SmtpResponseCategory.Unknown;
            var controlHosts = controlResults
                .Select(SmtpRecipientEvidencePolicy.MxHost)
                .Where(host => host is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var controlMx = controlHosts.Length == 1
                ? controlHosts[0]
                : null;
            var controlObservedAt = controlResults
                .Select(SmtpRecipientEvidencePolicy.RecipientObservedAt)
                .Where(timestamp => timestamp is not null)
                .Cast<DateTimeOffset>()
                .DefaultIfEmpty(domain.CatchAll.ObservedAt ?? DateTimeOffset.UtcNow)
                .Max();
            await observationStore.RecordAsync(new ValidationObservation(
                domain.Domain,
                ValidationObservationType.CatchAllProbe,
                observationProvider,
                controlMx,
                domain.CatchAll.Status,
                domain.CatchAll.Confidence,
                catchAllCategory,
                controlObservedAt,
                controlResults.Sum(result => result.Evidence?.ElapsedMilliseconds ?? 0),
                accepted,
                controlResults.Length,
                rejected,
                domain.Provider.GatewayProvider,
                domain.Provider.TopologyFingerprint,
                ObservationSessionId: observationSessionId,
                CorrelatedTargetResponseCategory: observedRecipientCategory,
                CorrelatedTargetObservedAt: targetObservedAt,
                CorrelatedTargetMxHost: targetMx,
                CorrelatedTargetRecipientEvidenceQualified: targetRecipientQualified), cancellationToken);
        }

        if (mailbox.Status != SmtpMailboxStatus.NotAttempted || mailbox.Evidence?.Reputation is not null)
        {
            await observationStore.RecordAsync(new ValidationObservation(
                domain.Domain,
                mailbox.Status == SmtpMailboxStatus.NotAttempted
                    ? ValidationObservationType.ReputationDecision
                    : ValidationObservationType.MailboxProbe,
                observationProvider,
                selectedMx,
                domain.CatchAll.Status,
                domain.CatchAll.Confidence,
                observedRecipientCategory,
                mailbox.Evidence?.Timestamp ?? DateTimeOffset.UtcNow,
                mailbox.Evidence?.ElapsedMilliseconds ?? (long)mailbox.ConnectionDuration.TotalMilliseconds,
                GatewayProvider: domain.Provider.GatewayProvider,
                TopologyFingerprint: domain.Provider.TopologyFingerprint,
                Reputation: mailbox.Evidence?.Reputation,
                ObservationSessionId: observationSessionId,
                RecipientEvidenceQualified: targetRecipientQualified,
                RecipientEvidenceContested: targetEvidenceContested), cancellationToken);
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
