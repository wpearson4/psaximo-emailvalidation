using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core;

public sealed class ValidationSingleFlight : IValidationSingleFlight
{
    private readonly ConcurrentDictionary<string, Flight> _operations =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<EmailValidationResult> ExecuteAsync(
        string key,
        Func<CancellationToken, Task<EmailValidationResult>> factory,
        CancellationToken cancellationToken = default)
    {
        var operation = _operations.GetOrAdd(key, _ => new Flight(factory));
        Interlocked.Increment(ref operation.Waiters);
        var task = operation.Task.Value;
        _ = task.ContinueWith(
            _ =>
            {
                _operations.TryRemove(new KeyValuePair<string, Flight>(key, operation));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Decrement(ref operation.Waiters) == 0 && !task.IsCompleted)
            {
                _operations.TryRemove(new KeyValuePair<string, Flight>(key, operation));
                operation.Cancellation.Cancel();
            }
            if (task.IsCompleted)
                _operations.TryRemove(new KeyValuePair<string, Flight>(key, operation));
        }
    }

    private sealed class Flight
    {
        public Flight(Func<CancellationToken, Task<EmailValidationResult>> factory)
        {
            Cancellation = new CancellationTokenSource();
            Task = new Lazy<Task<EmailValidationResult>>(
                () => factory(Cancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public CancellationTokenSource Cancellation { get; }
        public Lazy<Task<EmailValidationResult>> Task { get; }
        public int Waiters;
    }
}

public sealed class ValidationResultReusePolicy(IOptions<EmailValidationOptions> options) : IValidationResultReusePolicy
{
    private readonly ResultReuseOptions _options = options.Value.ResultReuse;

    public bool CanReuse(
        MailboxIntelligence intelligence,
        DomainIntelligence? currentDomain,
        EmailValidationRequest request,
        ValidationPolicyVersions currentPolicy,
        DateTimeOffset now)
    {
        if (!_options.Enabled || request.EnableSmtp && !intelligence.UsedLiveSmtp) return false;
        if (request.Verbose && intelligence.LastResult.Diagnostics is null) return false;
        if (intelligence.Policy != currentPolicy) return false;
        if (currentDomain is null || currentDomain.EvidenceExpiresAt is not { } domainExpiresAt || domainExpiresAt <= now)
            return false;
        if (!string.Equals(currentDomain.Provider.TopologyFingerprint, intelligence.MxTopologyFingerprint, StringComparison.Ordinal))
            return false;

        var lifetime = intelligence.PreviousStatus switch
        {
            EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid =>
                TimeSpan.FromMinutes(Math.Max(0, _options.StrongPositiveMinutes)),
            EmailValidationStatus.Invalid or EmailValidationStatus.LikelyInvalid =>
                TimeSpan.FromMinutes(Math.Max(0, _options.StrongNegativeMinutes)),
            EmailValidationStatus.Risky when intelligence.PreviousMailboxResult is
                SmtpMailboxStatus.Accepted or SmtpMailboxStatus.MailboxFull =>
                TimeSpan.FromMinutes(Math.Max(0, _options.RiskyMinutes)),
            _ => TimeSpan.Zero
        };
        return lifetime > TimeSpan.Zero && now - intelligence.LastValidatedAt <= lifetime;
    }
}

public sealed class ConfidenceCalibrationService(IDeliveryOutcomeStore outcomes) : IConfidenceCalibrationService
{
    public async Task<CalibrationResult> EvaluateAsync(
        CalibrationQuery query,
        CancellationToken cancellationToken = default)
    {
        var records = await outcomes.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        var usable = records.Where(record => record.ActualOutcome is
            DeliveryOutcomeKind.Delivered or DeliveryOutcomeKind.HardBounce or DeliveryOutcomeKind.SoftBounce).ToArray();
        var metrics = Calculate(usable);
        var bands = Enumerable.Range(0, 10)
            .Select(index => (Min: index / 10d, Max: (index + 1) / 10d))
            .Select(band =>
            {
                var samples = usable.Where(item => item.Prediction.PredictedConfidence >= band.Min &&
                    (band.Max == 1 ? item.Prediction.PredictedConfidence <= band.Max : item.Prediction.PredictedConfidence < band.Max)).ToArray();
                var bandMetrics = Calculate(samples);
                return new ConfidenceBandMetrics(
                    band.Min, band.Max, samples.Length, bandMetrics.DeliveryRate,
                    bandMetrics.HardBounceRate, bandMetrics.CalibrationError);
            })
            .Where(item => item.SampleCount > 0)
            .ToArray();

        // Expose aggregates immediately, but do not claim statistical calibration from
        // sparse or heuristic-only observations.
        var calibrated = usable.Length >= 1000 &&
            usable.All(item => item.Prediction.ConfidenceType == ConfidenceType.CalibratedProbability);
        return new CalibrationResult(
            query,
            metrics,
            bands,
            calibrated,
            calibrated
                ? "The cohort contains sufficient calibrated outcome observations."
                : "Aggregate outcome statistics only; the confidence score remains heuristic or the sample is insufficient.");
    }

    internal static CalibrationMetrics Calculate(IReadOnlyCollection<DeliveryOutcomeRecord> records)
    {
        if (records.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var scored = records.Where(item => item.ActualOutcome is
            DeliveryOutcomeKind.Delivered or DeliveryOutcomeKind.HardBounce or DeliveryOutcomeKind.SoftBounce).ToArray();
        var delivered = records.Count(item => item.ActualOutcome == DeliveryOutcomeKind.Delivered);
        var hardBounce = records.Count(item => item.ActualOutcome == DeliveryOutcomeKind.HardBounce);
        var softBounce = records.Count(item => item.ActualOutcome == DeliveryOutcomeKind.SoftBounce);
        var predictedPositive = records.Where(item => IsPositive(item.Prediction.PredictedStatus)).ToArray();
        var predictedNegative = records.Where(item => IsNegative(item.Prediction.PredictedStatus)).ToArray();
        var truePositive = predictedPositive.Count(item => item.ActualOutcome == DeliveryOutcomeKind.Delivered);
        var falsePositive = predictedPositive.Count(item => item.ActualOutcome == DeliveryOutcomeKind.HardBounce);
        var falseNegative = predictedNegative.Count(item => item.ActualOutcome == DeliveryOutcomeKind.Delivered);
        var allDelivered = records.Count(item => item.ActualOutcome == DeliveryOutcomeKind.Delivered);
        var brier = scored.Length == 0 ? 0 : scored.Average(item =>
        {
            var actual = item.ActualOutcome == DeliveryOutcomeKind.Delivered ? 1d : 0d;
            return Math.Pow(DeliveryProbability(item.Prediction) - actual, 2);
        });
        var calibrationError = scored.Length == 0 ? 0 : scored
            .GroupBy(item => Math.Min(9, (int)(DeliveryProbability(item.Prediction) * 10)))
            .Sum(band => band.Count() / (double)scored.Length * Math.Abs(
                band.Average(item => DeliveryProbability(item.Prediction)) -
                band.Count(item => item.ActualOutcome == DeliveryOutcomeKind.Delivered) / (double)band.Count()));
        return new(
            records.Count,
            Rate(delivered, records.Count),
            Rate(hardBounce, records.Count),
            Rate(softBounce, records.Count),
            Rate(falsePositive, predictedPositive.Length),
            Rate(falseNegative, predictedNegative.Length),
            Rate(truePositive, truePositive + falsePositive),
            Rate(truePositive, allDelivered),
            Round(brier),
            Round(calibrationError));
    }

    private static bool IsPositive(EmailValidationStatus status) =>
        status is EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid;

    private static bool IsNegative(EmailValidationStatus status) =>
        status is EmailValidationStatus.Invalid or EmailValidationStatus.LikelyInvalid;

    private static double DeliveryProbability(ValidationPredictionSnapshot prediction) => prediction.PredictedStatus switch
    {
        EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid => prediction.PredictedConfidence,
        EmailValidationStatus.Invalid or EmailValidationStatus.LikelyInvalid => 1 - prediction.PredictedConfidence,
        _ => 0.5
    };

    private static double Rate(int numerator, int denominator) => denominator == 0 ? 0 : Round(numerator / (double)denominator);
    private static double Round(double value) => Math.Round(value, 4);
}

public sealed class ExistingIntelligenceRiskDataSource : IRiskDataSource
{
    public Task<RiskDataResult> LookupAsync(EmailRiskContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reasons = new List<MailingRiskReason>();
        var evidence = new List<EvidenceProvenance>();
        var address = context.Address;
        var domain = context.Domain;

        Add(context.Checks.DisposableDomain, MailingRiskReason.DisposableAddress, "DisposableAddress", 0.95);
        Add(context.Checks.RoleAccount, MailingRiskReason.RoleAccount, "RoleAccount", 0.95);
        Add(domain?.ToxicDomain.Status is ToxicDomainStatus.KnownToxic or ToxicDomainStatus.LikelyToxic,
            MailingRiskReason.ToxicDomain, "ToxicDomain", domain?.ToxicDomain.Confidence ?? 0);
        Add(address?.SpamTrapRisk.Status is SpamTrapRiskStatus.PossibleSpamTrap or
            SpamTrapRiskStatus.LikelySpamTrap or SpamTrapRiskStatus.KnownSpamTrap,
            MailingRiskReason.SpamTrapIndicator, "SpamTrapIndicator", address?.SpamTrapRisk.Confidence ?? 0);
        Add(address?.AbuseRisk.Status == AbuseRiskStatus.KnownRisk,
            MailingRiskReason.KnownAbuse, "KnownAbuse", address?.AbuseRisk.Confidence ?? 0);
        Add(address?.Suppression.Status == SuppressionStatus.Suppressed,
            MailingRiskReason.KnownSuppression, "KnownSuppression", 0.99);

        var level = reasons.Any(reason => reason is MailingRiskReason.KnownSuppression or
            MailingRiskReason.KnownAbuse or MailingRiskReason.SpamTrapIndicator or MailingRiskReason.ToxicDomain)
            ? MailingRiskLevel.High
            : reasons.Count > 0 ? MailingRiskLevel.Medium
            : context.DeliverabilityStatus is EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid
                ? MailingRiskLevel.Low : MailingRiskLevel.Unknown;
        return Task.FromResult(new RiskDataResult("ExistingIntelligence", level, reasons, evidence));

        void Add(bool condition, MailingRiskReason reason, string signal, double confidence)
        {
            if (!condition) return;
            reasons.Add(reason);
            evidence.Add(new(signal, EvidenceSource.ConfiguredIntelligenceProvider, confidence,
                $"{signal} was supplied by configured intelligence; it was not inferred from SMTP acceptance."));
        }
    }
}

public sealed class PersistentSuppressionRiskDataSource(IGlobalSuppressionStore suppressions) : IRiskDataSource
{
    public async Task<RiskDataResult> LookupAsync(EmailRiskContext context, CancellationToken cancellationToken = default)
    {
        var match = await suppressions.GetAsync(context.NormalizedEmail, cancellationToken).ConfigureAwait(false);
        return match is null
            ? new("PersistentSuppression", MailingRiskLevel.Unknown, [], [])
            : new("PersistentSuppression", MailingRiskLevel.High, [MailingRiskReason.KnownSuppression],
                [new EvidenceProvenance("KnownSuppression", EvidenceSource.HistoricalObservation, 0.99,
                    $"Persisted suppression source: {match.Source}; reason: {match.Reason}.")]);
    }
}

public sealed class EmailRiskIntelligence(IEnumerable<IRiskDataSource> sources) : IEmailRiskIntelligence
{
    public async Task<EmailRiskResult> EvaluateAsync(EmailRiskContext context, CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(sources.Select(source => source.LookupAsync(context, cancellationToken)))
            .ConfigureAwait(false);
        var level = results.Select(item => item.Level).DefaultIfEmpty(MailingRiskLevel.Unknown).MaxBy(Rank);
        return new(
            context.DeliverabilityStatus,
            context.DeliverabilityConfidence,
            level,
            results.SelectMany(item => item.Reasons).Distinct().ToArray(),
            results.SelectMany(item => item.Evidence).ToArray());
    }

    private static int Rank(MailingRiskLevel level) => level switch
    {
        MailingRiskLevel.High => 3,
        MailingRiskLevel.Medium => 2,
        MailingRiskLevel.Low => 1,
        _ => 0
    };
}

public sealed class ValidationQualityMetrics : IValidationQualityMetrics
{
    private readonly object _gate = new();
    private readonly Dictionary<EmailValidationStatus, long> _statuses =
        Enum.GetValues<EmailValidationStatus>().ToDictionary(status => status, _ => 0L);
    private readonly Dictionary<MailProvider, ProviderAccumulator> _providers = [];
    private long _total;
    private long _blocked;
    private long _catchAll;
    private long _disposable;
    private long _typo;
    private long _suppression;

    public void Record(EmailValidationResult result)
    {
        lock (_gate)
        {
            _total++;
            _statuses[result.Status]++;
            if (IsPolicyBlocked(result)) _blocked++;
            if (result.Checks.CatchAll == CatchAllStatus.LikelyCatchAll) _catchAll++;
            if (result.Checks.DisposableDomain) _disposable++;
            if (result.AddressIntelligence?.Typo.TypoDetected == true) _typo++;
            if (result.MailingRisk?.RiskReasons.Contains(MailingRiskReason.KnownSuppression) == true) _suppression++;
            if (!_providers.TryGetValue(result.MailProvider, out var provider))
                _providers[result.MailProvider] = provider = new ProviderAccumulator();
            provider.Total++;
            if (result.Status == EmailValidationStatus.Unknown) provider.Unknown++;
            if (IsPolicyBlocked(result)) provider.PolicyBlocked++;
            if (result.ProviderValidation?.EffectiveCategory == SmtpResponseCategory.RecipientRejected)
                provider.RecipientRejected++;
            if (result.Checks.CatchAll == CatchAllStatus.LikelyCatchAll) provider.CatchAll++;
            if (result.ProviderValidation is not null)
            {
                provider.ReliabilitySamples++;
                provider.ReliabilityTotal += result.ProviderValidation.VerificationReliability;
            }
            provider.LatencyTotal += result.DurationMs;
        }
    }

    public ValidationQualitySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var rates = _statuses.ToDictionary(item => item.Key, item => Rate(item.Value, _total));
            var providers = _providers.Select(item => new ProviderQualitySnapshot(
                item.Key,
                item.Value.Total,
                Rate(item.Value.Unknown, item.Value.Total),
                Rate(item.Value.PolicyBlocked, item.Value.Total),
                Rate(item.Value.RecipientRejected, item.Value.Total),
                Rate(item.Value.CatchAll, item.Value.Total),
                Average(item.Value.ReliabilityTotal, item.Value.ReliabilitySamples),
                Average(item.Value.LatencyTotal, item.Value.Total))).ToArray();
            return new(
                _total,
                rates,
                Rate(_blocked, _total),
                Rate(_catchAll, _total),
                Rate(_disposable, _total),
                Rate(_typo, _total),
                Rate(_suppression, _total),
                providers);
        }
    }

    private static bool IsPolicyBlocked(EmailValidationResult result) =>
        result.ProviderValidation?.EffectiveCategory == SmtpResponseCategory.VerificationBlocked ||
        result.ReasonCodes.Contains(ReasonCode.PolicyBlock);

    private static double Average(double total, long count) => count == 0 ? 0 : Math.Round(total / count, 4);
    private static double Rate(long count, long total) => total == 0 ? 0 : Math.Round(count / (double)total, 4);

    private sealed class ProviderAccumulator
    {
        public long Total;
        public long Unknown;
        public long PolicyBlocked;
        public long RecipientRejected;
        public long CatchAll;
        public long ReliabilitySamples;
        public double ReliabilityTotal;
        public double LatencyTotal;
    }
}

public static class ValidationSubStatusMapper
{
    public static DetailedStatus Map(EmailValidationResult result)
    {
        if (result.MailingRisk?.RiskReasons.Contains(MailingRiskReason.KnownSuppression) == true)
            return DetailedStatus.KnownSuppression;
        if (result.AddressIntelligence?.Typo.TypoDetected == true) return DetailedStatus.TypoDetected;
        if (result.DomainIntelligence?.Dns.ExplicitNullMx == true) return DetailedStatus.NullMx;
        if (result.ReasonCodes.Contains(ReasonCode.DomainNotFound)) return DetailedStatus.DomainNotFound;
        if (result.ReasonCodes.Contains(ReasonCode.NoMailExchanger)) return DetailedStatus.NoMailExchanger;
        if (result.ReasonCodes.Contains(ReasonCode.ProviderVerificationBlocked)) return DetailedStatus.ProviderVerificationBlocked;
        if (result.ReasonCodes.Contains(ReasonCode.SenderIdentityRejected)) return DetailedStatus.SenderIdentityRejected;
        if (result.ReasonCodes.Contains(ReasonCode.PolicyBlock)) return DetailedStatus.PolicyBlocked;
        if (result.DetailedStatuses.Contains(DetailedStatus.MailboxNotFound)) return DetailedStatus.MailboxNotFound;
        if (result.ReasonCodes.Contains(ReasonCode.MailboxRejected)) return DetailedStatus.RecipientRejected;
        if (result.Checks.CatchAll == CatchAllStatus.LikelyCatchAll) return DetailedStatus.LikelyCatchAll;
        if (result.Checks.DisposableDomain) return DetailedStatus.DisposableAddress;
        if (result.Checks.RoleAccount) return DetailedStatus.RoleAccount;
        return result.DetailedStatus;
    }
}

public sealed class IntelligenceEmailValidator(
    IEmailValidationExecutor inner,
    IEmailNormalizer normalizer,
    IValidationIntelligenceStore store,
    IValidationSingleFlight singleFlight,
    IValidationResultReusePolicy reusePolicy,
    IEmailRiskIntelligence riskIntelligence,
    IValidationQualityMetrics qualityMetrics,
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider) : IEmailValidator
{
    private readonly ValidationPolicyVersions _policy = options.Value.Policy.ToVersions();

    public async Task<EmailValidationResult> ValidateAsync(
        string email,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = normalizer.Normalize(email);
        if (!normalized.IsValid)
        {
            var invalid = await inner.ValidateAsync(email, request, cancellationToken).ConfigureAwait(false);
            return await EnrichAsync(invalid, reused: false, cancellationToken).ConfigureAwait(false);
        }

        var key = $"{normalized.NormalizedEmail}|smtp:{request.EnableSmtp}|verbose:{request.Verbose}|{_policy}";
        return await singleFlight.ExecuteAsync(key, async operationToken =>
        {
            var now = timeProvider.GetUtcNow();
            var existingTask = store.GetMailboxAsync(normalized.NormalizedEmail!, operationToken);
            var domainTask = store.GetDomainAsync(normalized.Domain!, operationToken);
            await Task.WhenAll(existingTask, domainTask).ConfigureAwait(false);
            var existing = await existingTask.ConfigureAwait(false);
            var domain = await domainTask.ConfigureAwait(false);
            if (existing is not null && reusePolicy.CanReuse(existing, domain, request, _policy, now))
            {
                var reused = existing.LastResult with { Email = email, DurationMs = 0 };
                return await EnrichAsync(reused, reused: true, operationToken).ConfigureAwait(false);
            }

            var live = await inner.ValidateAsync(email, request, operationToken).ConfigureAwait(false);
            var enriched = await EnrichAsync(live, reused: false, operationToken).ConfigureAwait(false);
            if (enriched.NormalizedEmail is not null)
            {
                if (enriched.DomainIntelligence is not null)
                    await store.SaveDomainAsync(enriched.DomainIntelligence, operationToken).ConfigureAwait(false);
                await store.SaveMailboxAsync(ToMailboxIntelligence(enriched, request.EnableSmtp), operationToken)
                    .ConfigureAwait(false);
            }
            return enriched;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmailValidationResult> EnrichAsync(
        EmailValidationResult result,
        bool reused,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var validatedAt = result.Metadata?.ValidatedAt ?? now;
        EmailRiskResult? risk = result.MailingRisk;
        if (result.NormalizedEmail is not null)
        {
            risk = await riskIntelligence.EvaluateAsync(new EmailRiskContext(
                result.NormalizedEmail,
                result.Status,
                result.Confidence,
                result.Checks,
                result.DomainIntelligence,
                result.AddressIntelligence), cancellationToken).ConfigureAwait(false);
        }
        var staged = result with
        {
            MailingRisk = risk,
            Metadata = new ValidationResultMetadata(
                _policy,
                validatedAt,
                reused,
                reused ? now : null,
                result.Provider?.TopologyFingerprint ?? result.DomainIntelligence?.Provider.TopologyFingerprint)
        };
        var subStatus = ValidationSubStatusMapper.Map(staged);
        var enriched = staged with
        {
            SubStatus = subStatus,
            SubStatuses = staged.DetailedStatuses.Append(subStatus).Distinct().ToArray()
        };
        qualityMetrics.Record(enriched);
        return enriched;
    }

    private MailboxIntelligence ToMailboxIntelligence(EmailValidationResult result, bool usedLiveSmtp)
    {
        var at = result.Metadata!.ValidatedAt;
        var positive = result.Status is EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid;
        var negative = result.Status is EmailValidationStatus.Invalid or EmailValidationStatus.LikelyInvalid;
        return new MailboxIntelligence
        {
            NormalizedEmail = result.NormalizedEmail!,
            PreviousStatus = result.Status,
            PreviousMailboxResult = result.Checks.Mailbox,
            PreviousConfidence = result.Confidence,
            PreviousConfidenceType = result.ConfidenceType,
            LastValidatedAt = at,
            LastStrongPositiveEvidenceAt = positive ? at : null,
            LastStrongNegativeEvidenceAt = negative ? at : null,
            ProviderAtValidation = result.MailProvider,
            Policy = _policy,
            ReasonCodes = result.ReasonCodes,
            MxTopologyFingerprint = result.Metadata.MxTopologyFingerprint,
            UsedLiveSmtp = usedLiveSmtp,
            LastResult = SanitizeForPersistence(result)
        };
    }

    private static EmailValidationResult SanitizeForPersistence(EmailValidationResult result) => result with
    {
        SmtpEvidence = null,
        SmtpSessionEvidence = null,
        MxValidation = null,
        CatchAllEvidence = result.CatchAllEvidence is null
            ? null
            : result.CatchAllEvidence with { ProbeResults = [] },
        Diagnostics = result.Diagnostics is null ? null : result.Diagnostics with { Detail = null }
    };
}
