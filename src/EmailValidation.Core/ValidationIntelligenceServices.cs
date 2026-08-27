using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
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
        var execution = await ExecuteWithStatusAsync(key, factory, cancellationToken).ConfigureAwait(false);
        return execution.Result;
    }

    public async Task<ValidationSingleFlightResult> ExecuteWithStatusAsync(
        string key,
        Func<CancellationToken, Task<EmailValidationResult>> factory,
        CancellationToken cancellationToken = default)
    {
        var candidate = new Flight(factory);
        var operation = _operations.GetOrAdd(key, candidate);
        var joined = !ReferenceEquals(candidate, operation);
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
            var result = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ValidationSingleFlightResult(result, joined);
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

    public int ActiveCount => _operations.Count;

    private sealed class Flight
    {
        public Flight(Func<CancellationToken, Task<EmailValidationResult>> factory)
        {
            Cancellation = new CancellationTokenSource();
            Task = new Lazy<Task<EmailValidationResult>>(
                () => InvokeFactoryAsync(factory, Cancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public CancellationTokenSource Cancellation { get; }
        public Lazy<Task<EmailValidationResult>> Task { get; }
        public int Waiters;

        private static async Task<EmailValidationResult> InvokeFactoryAsync(
            Func<CancellationToken, Task<EmailValidationResult>> factory,
            CancellationToken cancellationToken) =>
            await factory(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ValidationResultReusePolicy(IOptions<EmailValidationOptions> options) : IValidationResultReusePolicy
{
    private readonly ResultReuseOptions _options = options.Value.ResultReuse;

    public ValidationReuseDecision Evaluate(
        MailboxIntelligence intelligence,
        DomainIntelligence? currentDomain,
        EmailValidationRequest request,
        ValidationPolicyVersions currentPolicy,
        DateTimeOffset now)
    {
        if (!_options.Enabled)
            return Reject(ValidationReuseAction.CannotReuse, ValidationReuseRejectionReason.Disabled);
        if (request.EnableSmtp && !intelligence.UsedLiveSmtp)
            return Reject(ValidationReuseAction.RevalidateMailboxOnly, ValidationReuseRejectionReason.SmtpEvidenceRequired);
        if (request.Verbose && intelligence.LastResult.Diagnostics is null)
            return Reject(ValidationReuseAction.CannotReuse, ValidationReuseRejectionReason.VerboseDiagnosticsUnavailable);
        if (intelligence.Policy != currentPolicy)
            return Reject(ValidationReuseAction.CannotReuse, ValidationReuseRejectionReason.PolicyVersion);
        if (currentDomain is null || currentDomain.EvidenceExpiresAt is not { } domainExpiresAt || domainExpiresAt <= now)
            return Reject(ValidationReuseAction.RevalidateDomainAndMailbox, ValidationReuseRejectionReason.DomainStale);
        if (!string.Equals(currentDomain.Provider.TopologyFingerprint, intelligence.MxTopologyFingerprint, StringComparison.Ordinal))
            return Reject(ValidationReuseAction.RevalidateMailboxOnly, ValidationReuseRejectionReason.MxTopology);

        var (lifetime, evidenceAt) = intelligence.PreviousStatus switch
        {
            EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid or EmailValidationStatus.CatchAll
                when intelligence.PreviousMailboxResult == SmtpMailboxStatus.Accepted =>
                (TimeSpan.FromMinutes(Math.Max(0, _options.StrongPositiveMinutes)),
                    intelligence.LastStrongPositiveEvidenceAt ?? intelligence.LastValidatedAt),
            EmailValidationStatus.Invalid or EmailValidationStatus.LikelyInvalid
                when intelligence.PreviousMailboxResult == SmtpMailboxStatus.Rejected =>
                (TimeSpan.FromMinutes(Math.Max(0, _options.StrongNegativeMinutes)),
                    intelligence.LastStrongNegativeEvidenceAt ?? intelligence.LastValidatedAt),
            EmailValidationStatus.Risky when intelligence.PreviousMailboxResult is
                SmtpMailboxStatus.Accepted or SmtpMailboxStatus.MailboxFull =>
                (TimeSpan.FromMinutes(Math.Max(0, _options.RiskyMinutes)), intelligence.LastValidatedAt),
            EmailValidationStatus.Unknown when IsTransient(intelligence) =>
                (TimeSpan.FromMinutes(Math.Max(0, _options.TransientMinutes)), intelligence.LastValidatedAt),
            _ => (TimeSpan.Zero, intelligence.LastValidatedAt)
        };
        if (lifetime <= TimeSpan.Zero)
            return Reject(ValidationReuseAction.CannotReuse, ValidationReuseRejectionReason.ResultNotReusable);

        var remaining = lifetime - (now - evidenceAt);
        remaining = remaining < domainExpiresAt - now ? remaining : domainExpiresAt - now;
        return remaining > TimeSpan.Zero
            ? new ValidationReuseDecision(ValidationReuseAction.Reuse, ValidationReuseRejectionReason.None, remaining)
            : Reject(ValidationReuseAction.RevalidateMailboxOnly, ValidationReuseRejectionReason.Stale);
    }

    public bool CanReuse(
        MailboxIntelligence intelligence,
        DomainIntelligence? currentDomain,
        EmailValidationRequest request,
        ValidationPolicyVersions currentPolicy,
        DateTimeOffset now) => Evaluate(intelligence, currentDomain, request, currentPolicy, now).CanReuse;

    private static bool IsTransient(MailboxIntelligence intelligence) =>
        intelligence.PreviousMailboxResult is SmtpMailboxStatus.Blocked or SmtpMailboxStatus.TemporaryFailure or
            SmtpMailboxStatus.Timeout or SmtpMailboxStatus.ConnectionFailure ||
        intelligence.ReasonCodes.Any(reason => reason is
            ReasonCode.ProviderVerificationBlocked or ReasonCode.ProviderBlockedVerification or
            ReasonCode.TemporarySmtpFailure or ReasonCode.TemporaryFailure or ReasonCode.SmtpTimeout or
            ReasonCode.Timeout or ReasonCode.Greylisted or ReasonCode.RateLimited or ReasonCode.LocalCooldown);

    private static ValidationReuseDecision Reject(
        ValidationReuseAction action,
        ValidationReuseRejectionReason reason) => new(action, reason, TimeSpan.Zero);
}

public sealed class ValidationPersistenceMetrics : IValidationPersistenceMetrics, IDisposable
{
    private readonly Meter _meter = new("EmailValidation.Persistence", "1.0.0");
    private readonly Counter<long> _reads;
    private readonly Counter<long> _hits;
    private readonly Counter<long> _misses;
    private readonly Counter<long> _writeSuccesses;
    private readonly Counter<long> _writeFailures;
    private readonly Counter<long> _mailboxReuses;
    private readonly Counter<long> _domainReuses;
    private readonly Counter<long> _staleMailboxRefreshes;
    private readonly Counter<long> _liveSmtpAvoided;
    private readonly Counter<long> _validationRequests;
    private readonly Counter<long> _smtpValidationRequired;
    private readonly Counter<long> _smtpValidationAvoided;
    private readonly Counter<long> _smtpValidationPerformed;
    private readonly Counter<long> _memoryCacheHits;
    private readonly Counter<long> _persistentReuseHits;
    private readonly Counter<long> _reuseMisses;
    private readonly Counter<long> _liveValidations;
    private readonly Counter<long> _singleFlightLeaders;
    private readonly Counter<long> _singleFlightJoiners;
    private readonly Counter<long> _cacheWrites;
    private readonly Counter<long> _cacheInvalidations;
    private readonly Counter<long> _liveValidationAvoided;
    private readonly Counter<long> _catchAllDiscovered;
    private readonly Counter<long> _catchAllReuseHits;
    private readonly Counter<long> _catchAllProbesAvoided;
    private readonly Counter<long> _mailboxProbesAvoidedDueToCatchAll;
    private readonly Counter<long> _catchAllRefreshed;
    private readonly Counter<long> _catchAllExpired;
    private readonly Counter<long> _catchAllClassificationChanged;
    private readonly Histogram<double> _queryLatency;
    private long _readCount;
    private long _hitCount;
    private long _missCount;
    private long _writeSuccessCount;
    private long _writeFailureCount;
    private long _mailboxReuseCount;
    private long _domainReuseCount;
    private long _staleMailboxRefreshCount;
    private long _liveSmtpAvoidedCount;
    private long _validationRequestCount;
    private long _memoryCacheHitCount;
    private long _persistentReuseHitCount;
    private long _reuseMissCount;
    private long _liveValidationCount;
    private long _singleFlightLeaderCount;
    private long _singleFlightJoinerCount;
    private long _cacheWriteCount;
    private long _cacheInvalidationCount;
    private long _staleRejectionCount;
    private long _policyVersionRejectionCount;
    private long _mxTopologyRejectionCount;
    private long _memoryCacheAvoidedCount;
    private long _singleFlightAvoidedCount;
    private long _catchAllDiscoveredCount;
    private long _catchAllReuseHitCount;
    private long _catchAllProbesAvoidedCount;
    private long _mailboxProbesAvoidedDueToCatchAllCount;
    private long _catchAllRefreshedCount;
    private long _catchAllExpiredCount;
    private long _catchAllClassificationChangedCount;
    private readonly Counter<long> _smtpUtf8Required;
    private readonly Counter<long> _smtpUtf8Unsupported;

    public ValidationPersistenceMetrics()
    {
        _reads = _meter.CreateCounter<long>("email_validation.persistence.reads");
        _hits = _meter.CreateCounter<long>("email_validation.persistence.hits");
        _misses = _meter.CreateCounter<long>("email_validation.persistence.misses");
        _writeSuccesses = _meter.CreateCounter<long>("email_validation.persistence.write.success");
        _writeFailures = _meter.CreateCounter<long>("email_validation.persistence.write.failure");
        _mailboxReuses = _meter.CreateCounter<long>("email_validation.persistence.mailbox_reuse");
        _domainReuses = _meter.CreateCounter<long>("email_validation.persistence.domain_reuse");
        _staleMailboxRefreshes = _meter.CreateCounter<long>("email_validation.persistence.stale_mailbox_refresh");
        _liveSmtpAvoided = _meter.CreateCounter<long>("email_validation.live_smtp.avoided");
        _validationRequests = _meter.CreateCounter<long>("email_validation.requests");
        _smtpValidationRequired = _meter.CreateCounter<long>("smtp_validation_required_total");
        _smtpValidationAvoided = _meter.CreateCounter<long>("smtp_validation_avoided_total");
        _smtpValidationPerformed = _meter.CreateCounter<long>("smtp_validation_performed_total");
        _memoryCacheHits = _meter.CreateCounter<long>("email_validation.result_cache.hits");
        _persistentReuseHits = _meter.CreateCounter<long>("email_validation.persistent_reuse.hits");
        _reuseMisses = _meter.CreateCounter<long>("email_validation.reuse.misses");
        _liveValidations = _meter.CreateCounter<long>("email_validation.live.executions");
        _singleFlightLeaders = _meter.CreateCounter<long>("email_validation.single_flight.leaders");
        _singleFlightJoiners = _meter.CreateCounter<long>("email_validation.single_flight.joiners");
        _cacheWrites = _meter.CreateCounter<long>("email_validation.result_cache.writes");
        _cacheInvalidations = _meter.CreateCounter<long>("email_validation.result_cache.invalidations");
        _liveValidationAvoided = _meter.CreateCounter<long>("email_validation.live.avoided");
        _catchAllDiscovered = _meter.CreateCounter<long>("email_validation.catch_all.domains_discovered");
        _catchAllReuseHits = _meter.CreateCounter<long>("email_validation.catch_all.domain_reuse_hits");
        _catchAllProbesAvoided = _meter.CreateCounter<long>("email_validation.catch_all.live_probes_avoided");
        _mailboxProbesAvoidedDueToCatchAll = _meter.CreateCounter<long>("email_validation.catch_all.mailbox_probes_avoided");
        _catchAllRefreshed = _meter.CreateCounter<long>("email_validation.catch_all.intelligence_refreshed");
        _catchAllExpired = _meter.CreateCounter<long>("email_validation.catch_all.intelligence_expired");
        _catchAllClassificationChanged = _meter.CreateCounter<long>("email_validation.catch_all.classification_changed");
        _smtpUtf8Required = _meter.CreateCounter<long>("email_validation.smtp_utf8.required");
        _smtpUtf8Unsupported = _meter.CreateCounter<long>("email_validation.smtp_utf8.unsupported");
        _queryLatency = _meter.CreateHistogram<double>("email_validation.persistence.query.duration", "ms");
    }

    public void RecordValidationRequest()
    {
        _validationRequests.Add(1);
        Interlocked.Increment(ref _validationRequestCount);
    }

    public void RecordSmtpValidationRequired() => _smtpValidationRequired.Add(1);
    public void RecordSmtpValidationAvoided() => _smtpValidationAvoided.Add(1);
    public void RecordSmtpValidationPerformed() => _smtpValidationPerformed.Add(1);

    public void RecordSmtpUtf8(bool required, bool supported)
    {
        if (!required) return;
        _smtpUtf8Required.Add(1);
        if (!supported) _smtpUtf8Unsupported.Add(1);
    }

    public void RecordRead(string recordType, bool found, TimeSpan elapsed)
    {
        var tags = new TagList { { "record.type", recordType } };
        _reads.Add(1, tags);
        _queryLatency.Record(elapsed.TotalMilliseconds, tags);
        Interlocked.Increment(ref _readCount);
        if (found)
        {
            _hits.Add(1, tags);
            Interlocked.Increment(ref _hitCount);
        }
        else
        {
            _misses.Add(1, tags);
            Interlocked.Increment(ref _missCount);
        }
    }

    public void RecordWrite(string recordType, bool succeeded)
    {
        var tags = new TagList { { "record.type", recordType } };
        if (succeeded)
        {
            _writeSuccesses.Add(1, tags);
            Interlocked.Increment(ref _writeSuccessCount);
        }
        else
        {
            _writeFailures.Add(1, tags);
            Interlocked.Increment(ref _writeFailureCount);
        }
    }

    public void RecordMailboxReuse(bool liveSmtpAvoided)
    {
        _mailboxReuses.Add(1);
        _persistentReuseHits.Add(1);
        Interlocked.Increment(ref _mailboxReuseCount);
        Interlocked.Increment(ref _persistentReuseHitCount);
        if (!liveSmtpAvoided) return;
        _liveSmtpAvoided.Add(1);
        _liveValidationAvoided.Add(1, new TagList { { "reason", "persistent_reuse" } });
        Interlocked.Increment(ref _liveSmtpAvoidedCount);
    }

    public void RecordMemoryCacheLookup(bool hit)
    {
        if (!hit) return;
        _memoryCacheHits.Add(1);
        _liveValidationAvoided.Add(1, new TagList { { "reason", "memory_cache" } });
        Interlocked.Increment(ref _memoryCacheHitCount);
        Interlocked.Increment(ref _memoryCacheAvoidedCount);
    }

    public void RecordReuseMiss(ValidationReuseRejectionReason reason)
    {
        _reuseMisses.Add(1, new TagList { { "reason", reason.ToString() } });
        Interlocked.Increment(ref _reuseMissCount);
        if (reason is ValidationReuseRejectionReason.Stale or ValidationReuseRejectionReason.DomainStale)
            Interlocked.Increment(ref _staleRejectionCount);
        if (reason == ValidationReuseRejectionReason.PolicyVersion)
            Interlocked.Increment(ref _policyVersionRejectionCount);
        if (reason == ValidationReuseRejectionReason.MxTopology)
            Interlocked.Increment(ref _mxTopologyRejectionCount);
    }

    public void RecordLiveValidation()
    {
        _liveValidations.Add(1);
        Interlocked.Increment(ref _liveValidationCount);
    }

    public void RecordSingleFlight(bool joinedExistingOperation)
    {
        if (joinedExistingOperation)
        {
            _singleFlightJoiners.Add(1);
            _liveValidationAvoided.Add(1, new TagList { { "reason", "single_flight" } });
            Interlocked.Increment(ref _singleFlightJoinerCount);
            Interlocked.Increment(ref _singleFlightAvoidedCount);
        }
        else
        {
            _singleFlightLeaders.Add(1);
            Interlocked.Increment(ref _singleFlightLeaderCount);
        }
    }

    public void RecordCacheWrite()
    {
        _cacheWrites.Add(1);
        Interlocked.Increment(ref _cacheWriteCount);
    }

    public void RecordCacheInvalidation()
    {
        _cacheInvalidations.Add(1);
        Interlocked.Increment(ref _cacheInvalidationCount);
    }

    public void RecordStaleMailboxRefresh()
    {
        _staleMailboxRefreshes.Add(1);
        Interlocked.Increment(ref _staleMailboxRefreshCount);
    }

    public void RecordDomainReuse()
    {
        _domainReuses.Add(1);
        Interlocked.Increment(ref _domainReuseCount);
    }

    public void RecordCatchAllDiscovered()
    {
        _catchAllDiscovered.Add(1);
        Interlocked.Increment(ref _catchAllDiscoveredCount);
    }

    public void RecordCatchAllReuse(bool catchAllProbeAvoided, bool mailboxProbeAvoided)
    {
        _catchAllReuseHits.Add(1);
        Interlocked.Increment(ref _catchAllReuseHitCount);
        if (catchAllProbeAvoided)
        {
            _catchAllProbesAvoided.Add(1);
            Interlocked.Increment(ref _catchAllProbesAvoidedCount);
        }
        if (mailboxProbeAvoided)
        {
            _mailboxProbesAvoidedDueToCatchAll.Add(1);
            Interlocked.Increment(ref _mailboxProbesAvoidedDueToCatchAllCount);
        }
    }

    public void RecordCatchAllRefreshed(bool expired, bool classificationChanged)
    {
        _catchAllRefreshed.Add(1);
        Interlocked.Increment(ref _catchAllRefreshedCount);
        if (expired)
        {
            _catchAllExpired.Add(1);
            Interlocked.Increment(ref _catchAllExpiredCount);
        }
        if (classificationChanged)
        {
            _catchAllClassificationChanged.Add(1);
            Interlocked.Increment(ref _catchAllClassificationChangedCount);
        }
    }

    public ValidationPersistenceSnapshot GetSnapshot() => new(
        Interlocked.Read(ref _validationRequestCount),
        Interlocked.Read(ref _readCount),
        Interlocked.Read(ref _hitCount),
        Interlocked.Read(ref _missCount),
        Interlocked.Read(ref _memoryCacheHitCount),
        Interlocked.Read(ref _persistentReuseHitCount),
        Interlocked.Read(ref _reuseMissCount),
        Interlocked.Read(ref _liveValidationCount),
        Interlocked.Read(ref _singleFlightLeaderCount),
        Interlocked.Read(ref _singleFlightJoinerCount),
        Interlocked.Read(ref _cacheWriteCount),
        Interlocked.Read(ref _cacheInvalidationCount),
        Interlocked.Read(ref _staleRejectionCount),
        Interlocked.Read(ref _policyVersionRejectionCount),
        Interlocked.Read(ref _mxTopologyRejectionCount),
        Interlocked.Read(ref _writeSuccessCount),
        Interlocked.Read(ref _writeFailureCount),
        Interlocked.Read(ref _mailboxReuseCount),
        Interlocked.Read(ref _domainReuseCount),
        Interlocked.Read(ref _staleMailboxRefreshCount),
        Interlocked.Read(ref _liveSmtpAvoidedCount),
        Interlocked.Read(ref _memoryCacheAvoidedCount),
        Interlocked.Read(ref _singleFlightAvoidedCount),
        Interlocked.Read(ref _catchAllDiscoveredCount),
        Interlocked.Read(ref _catchAllReuseHitCount),
        Interlocked.Read(ref _catchAllProbesAvoidedCount),
        Interlocked.Read(ref _mailboxProbesAvoidedDueToCatchAllCount),
        Interlocked.Read(ref _catchAllRefreshedCount),
        Interlocked.Read(ref _catchAllExpiredCount),
        Interlocked.Read(ref _catchAllClassificationChangedCount));

    public void Dispose() => _meter.Dispose();
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
        status is EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid or EmailValidationStatus.CatchAll;

    private static bool IsNegative(EmailValidationStatus status) =>
        status is EmailValidationStatus.Invalid or EmailValidationStatus.LikelyInvalid;

    private static double DeliveryProbability(ValidationPredictionSnapshot prediction) => prediction.PredictedStatus switch
    {
        EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid or EmailValidationStatus.CatchAll => prediction.PredictedConfidence,
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
            : context.DeliverabilityStatus is EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid or EmailValidationStatus.CatchAll
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
        var results = await Task.WhenAll(sources.Select(source => LookupSafelyAsync(source, context, cancellationToken)))
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

    private static async Task<RiskDataResult> LookupSafelyAsync(
        IRiskDataSource source,
        EmailRiskContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await source.LookupAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new RiskDataResult(
                source.GetType().Name,
                MailingRiskLevel.Unknown,
                [],
                []);
        }
    }
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
        if (result.ReasonCodes.Contains(ReasonCode.LocalCooldown)) return DetailedStatus.LocalCooldown;
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
    IValidationResultCache resultCache,
    IValidationSingleFlight singleFlight,
    IValidationResultReusePolicy reusePolicy,
    IEmailRiskIntelligence riskIntelligence,
    IValidationQualityMetrics qualityMetrics,
    IValidationPersistenceMetrics persistenceMetrics,
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider,
    ILogger<IntelligenceEmailValidator> logger) : IEmailValidator, IEmailValidationService
{
    private readonly ValidationPolicyVersions _policy = options.Value.Policy.ToVersions();
    private readonly ResultReuseOptions _reuseOptions = options.Value.ResultReuse;

    public async Task<EmailValidationResult> ValidateAsync(
        string email,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        persistenceMetrics.RecordValidationRequest();
        if (request.EnableSmtp) persistenceMetrics.RecordSmtpValidationRequired();
        var normalized = normalizer.Normalize(email);
        if (!normalized.IsValid)
        {
            persistenceMetrics.RecordLiveValidation();
            var invalid = await inner.ValidateAsync(email, request, cancellationToken).ConfigureAwait(false);
            return await EnrichAsync(invalid, ValidationResultSource.LiveValidation, cancellationToken)
                .ConfigureAwait(false);
        }

        var key = CreateExecutionKey(normalized.NormalizedEmail!, request);
        var cached = await GetCachedAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            if (request.EnableSmtp) persistenceMetrics.RecordSmtpValidationAvoided();
            return ReturnFromSource(cached, email, ValidationResultSource.MemoryCache);
        }

        var lookup = await LookupPersistentAsync(
            normalized.NormalizedEmail!, normalized.Domain!, request, cancellationToken).ConfigureAwait(false);
        RecordLookupDecision(lookup);
        if (lookup.Decision.CanReuse)
        {
            if (request.EnableSmtp) persistenceMetrics.RecordSmtpValidationAvoided();
            return await ReusePersistentAsync(email, key, lookup, cancellationToken).ConfigureAwait(false);
        }

        async Task<EmailValidationResult> ExecuteLeaderAsync(CancellationToken operationToken)
        {
            // A previous flight can finish after this request's outer misses but before
            // this request becomes the next leader. Recheck the hot cache before live work.
            var leaderCached = await GetCachedAsync(key, operationToken).ConfigureAwait(false);
            if (leaderCached is not null)
            {
                if (request.EnableSmtp) persistenceMetrics.RecordSmtpValidationAvoided();
                return ReturnFromSource(leaderCached, email, ValidationResultSource.MemoryCache);
            }

            persistenceMetrics.RecordLiveValidation();
            var live = await inner.ValidateAsync(email, request, operationToken).ConfigureAwait(false);
            var liveSource = live.Metadata?.ResultSource == ValidationResultSource.PersistentDomainIntelligence
                ? ValidationResultSource.PersistentDomainIntelligence
                : ValidationResultSource.LiveValidation;
            var enriched = await EnrichAsync(live, liveSource, operationToken)
                .ConfigureAwait(false);
            var reusableDomain = lookup.Domain?.EvidenceExpiresAt is { } expiresAt &&
                expiresAt > timeProvider.GetUtcNow();
            if (enriched.Diagnostics is not null)
            {
                enriched = enriched with
                {
                    Diagnostics = enriched.Diagnostics with
                    {
                        PersistentMailboxFound = lookup.Mailbox is not null,
                        PersistentDomainFound = lookup.Domain is not null,
                        PersistentMailboxFresh = false,
                        PersistentIntelligenceDecision = liveSource == ValidationResultSource.PersistentDomainIntelligence
                            ? "Reused catch-all domain intelligence; skipped catch-all and mailbox SMTP probes"
                            : reusableDomain
                            ? "Reused domain intelligence; performed mailbox validation"
                            : "Performed live validation"
                    }
                };
            }
            if (enriched.NormalizedEmail is not null)
            {
                var mailbox = ToMailboxIntelligence(
                    enriched,
                    request.EnableSmtp && enriched.Checks.Mailbox != SmtpMailboxStatus.NotAttempted);
                if (enriched.DomainIntelligence is not null)
                    await store.SaveDomainAsync(enriched.DomainIntelligence, operationToken).ConfigureAwait(false);
                await store.SaveMailboxAsync(mailbox, operationToken).ConfigureAwait(false);
                await TryRemoveCachedAsync(key, operationToken).ConfigureAwait(false);
                var cacheDecision = reusePolicy.Evaluate(
                    mailbox,
                    enriched.DomainIntelligence,
                    request,
                    _policy,
                    timeProvider.GetUtcNow());
                await CacheIfReusableAsync(key, enriched, cacheDecision, operationToken).ConfigureAwait(false);
            }
            logger.LogDebug(
                "Persistent intelligence decision: mailbox {MailboxState}, domain {DomainState}, decision {Decision}",
                lookup.Mailbox is null ? "missing" : "stale",
                reusableDomain ? "reused" : lookup.Domain is null ? "missing" : "stale",
                liveSource == ValidationResultSource.PersistentDomainIntelligence
                    ? "catch-all domain reuse without mailbox validation"
                    : reusableDomain ? "domain reuse with live mailbox validation" : "live validation");
            return enriched;
        }

        if (!_reuseOptions.SingleFlightEnabled)
            return await ExecuteLeaderAsync(cancellationToken).ConfigureAwait(false);

        var flight = await singleFlight.ExecuteWithStatusAsync(key, ExecuteLeaderAsync, cancellationToken)
            .ConfigureAwait(false);
        persistenceMetrics.RecordSingleFlight(flight.JoinedExistingOperation);
        if (request.EnableSmtp && flight.JoinedExistingOperation)
            persistenceMetrics.RecordSmtpValidationAvoided();
        return flight.JoinedExistingOperation
            ? ReturnFromSource(flight.Result, email, ValidationResultSource.JoinedInFlightValidation)
            : flight.Result with { Email = email };
    }

    private async Task<EmailValidationResult> EnrichAsync(
        EmailValidationResult result,
        ValidationResultSource source,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var validatedAt = result.Metadata?.ValidatedAt ?? now;
        var reused = source is ValidationResultSource.MemoryCache or ValidationResultSource.PersistentReuse or
            ValidationResultSource.PersistentDomainIntelligence;
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
                result.Metadata?.MxTopologyFingerprint ?? result.Provider?.TopologyFingerprint ??
                    result.DomainIntelligence?.Provider.TopologyFingerprint,
                source,
                now,
                reused ? now - validatedAt : null)
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

    private async Task<PersistenceLookup> LookupPersistentAsync(
        string normalizedEmail,
        string normalizedDomain,
        EmailValidationRequest request,
        CancellationToken cancellationToken)
    {
        var mailboxTask = store.GetMailboxAsync(normalizedEmail, cancellationToken);
        var domainTask = store.GetDomainAsync(normalizedDomain, cancellationToken);
        await Task.WhenAll(mailboxTask, domainTask).ConfigureAwait(false);
        var mailbox = await mailboxTask.ConfigureAwait(false);
        var domain = await domainTask.ConfigureAwait(false);
        var decision = mailbox is null
            ? new ValidationReuseDecision(
                ValidationReuseAction.CannotReuse,
                ValidationReuseRejectionReason.ResultNotReusable,
                TimeSpan.Zero)
            : reusePolicy.Evaluate(mailbox, domain, request, _policy, timeProvider.GetUtcNow());
        return new PersistenceLookup(mailbox, domain, decision);
    }

    private void RecordLookupDecision(PersistenceLookup lookup)
    {
        if (lookup.Decision.CanReuse) return;
        persistenceMetrics.RecordReuseMiss(lookup.Decision.RejectionReason);
        if (lookup.Mailbox is not null) persistenceMetrics.RecordStaleMailboxRefresh();
        if (lookup.Domain?.EvidenceExpiresAt is { } expiresAt && expiresAt > timeProvider.GetUtcNow())
            persistenceMetrics.RecordDomainReuse();
    }

    private async Task<EmailValidationResult> ReusePersistentAsync(
        string email,
        string key,
        PersistenceLookup lookup,
        CancellationToken cancellationToken)
    {
        persistenceMetrics.RecordMailboxReuse(liveSmtpAvoided: true);
        var stored = lookup.Mailbox!.LastResult with
        {
            Email = email,
            DurationMs = 0,
            Diagnostics = lookup.Mailbox.LastResult.Diagnostics is null
                ? null
                : lookup.Mailbox.LastResult.Diagnostics with
                {
                    PersistentMailboxFound = true,
                    PersistentDomainFound = lookup.Domain is not null,
                    PersistentMailboxFresh = true,
                    PersistentIntelligenceDecision = "Reused previous mailbox result"
                }
        };
        var reused = await EnrichAsync(stored, ValidationResultSource.PersistentReuse, cancellationToken)
            .ConfigureAwait(false);
        await CacheIfReusableAsync(key, reused, lookup.Decision, cancellationToken).ConfigureAwait(false);
        return reused;
    }

    private async Task<EmailValidationResult?> GetCachedAsync(string key, CancellationToken cancellationToken)
    {
        if (!_reuseOptions.Enabled || !_reuseOptions.MemoryCacheEnabled) return null;
        EmailValidationResult? cached;
        try
        {
            cached = await resultCache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableCacheFailure(exception))
        {
            logger.LogWarning(
                "Result cache read failed; validation will continue without the cache ({ErrorType})",
                exception.GetType().Name);
            return null;
        }
        persistenceMetrics.RecordMemoryCacheLookup(cached is not null);
        return cached;
    }

    private async Task CacheIfReusableAsync(
        string key,
        EmailValidationResult result,
        ValidationReuseDecision decision,
        CancellationToken cancellationToken)
    {
        if (!_reuseOptions.MemoryCacheEnabled || !decision.CanReuse) return;
        try
        {
            await resultCache.SetAsync(key, result, decision.RemainingLifetime, cancellationToken).ConfigureAwait(false);
            persistenceMetrics.RecordCacheWrite();
        }
        catch (Exception exception) when (IsRecoverableCacheFailure(exception))
        {
            logger.LogWarning(
                "Result cache write failed; validation result remains available ({ErrorType})",
                exception.GetType().Name);
        }
    }

    private async Task TryRemoveCachedAsync(string key, CancellationToken cancellationToken)
    {
        if (!_reuseOptions.MemoryCacheEnabled) return;
        try
        {
            await resultCache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            persistenceMetrics.RecordCacheInvalidation();
        }
        catch (Exception exception) when (IsRecoverableCacheFailure(exception))
        {
            logger.LogWarning(
                "Result cache invalidation failed; validation will continue ({ErrorType})",
                exception.GetType().Name);
        }
    }

    private static bool IsRecoverableCacheFailure(Exception exception) =>
        exception is IOException or InvalidOperationException or TimeoutException;

    private EmailValidationResult ReturnFromSource(
        EmailValidationResult result,
        string email,
        ValidationResultSource source)
    {
        var now = timeProvider.GetUtcNow();
        var metadata = result.Metadata;
        var validatedAt = metadata?.ValidatedAt ?? now;
        var reused = source is ValidationResultSource.MemoryCache or ValidationResultSource.PersistentReuse or
            ValidationResultSource.PersistentDomainIntelligence ||
            metadata?.Reused == true;
        var returned = result with
        {
            Email = email,
            DurationMs = source == ValidationResultSource.JoinedInFlightValidation ? result.DurationMs : 0,
            Metadata = new ValidationResultMetadata(
                metadata?.Policy ?? _policy,
                validatedAt,
                reused,
                reused ? now : metadata?.ReusedAt,
                metadata?.MxTopologyFingerprint,
                source,
                now,
                reused ? now - validatedAt : metadata?.ReuseAge),
            Diagnostics = result.Diagnostics is null ? null : result.Diagnostics with
            {
                PersistentIntelligenceDecision = source switch
                {
                    ValidationResultSource.MemoryCache => "Fresh result reused from memory cache",
                    ValidationResultSource.JoinedInFlightValidation => "Joined validation already in progress",
                    _ => result.Diagnostics.PersistentIntelligenceDecision
                }
            }
        };
        if (source == ValidationResultSource.MemoryCache) qualityMetrics.Record(returned);
        return returned;
    }

    private string CreateExecutionKey(string normalizedEmail, EmailValidationRequest request) =>
        $"{normalizedEmail}|smtp:{request.EnableSmtp}|verbose:{request.Verbose}|" +
        $"engine:{_policy.ValidationEngineVersion}|classification:{_policy.ClassificationPolicyVersion}|" +
        $"confidence:{_policy.ConfidenceModelVersion}|provider:{_policy.ProviderStrategyVersion}";

    private MailboxIntelligence ToMailboxIntelligence(EmailValidationResult result, bool usedLiveSmtp)
    {
        var at = result.Metadata!.ValidatedAt;
        var positive = result.Status is EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid or EmailValidationStatus.CatchAll;
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
        Email = result.NormalizedEmail ?? result.Email,
        SmtpEvidence = null,
        SmtpSessionEvidence = null,
        MxValidation = null,
        CatchAllEvidence = result.CatchAllEvidence is null
            ? null
            : result.CatchAllEvidence with { ProbeResults = [] },
        Diagnostics = result.Diagnostics is null ? null : result.Diagnostics with { Detail = null }
    };

    private sealed record PersistenceLookup(
        MailboxIntelligence? Mailbox,
        DomainIntelligence? Domain,
        ValidationReuseDecision Decision);
}
