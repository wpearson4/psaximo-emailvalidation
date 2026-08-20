using System.Diagnostics;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

/// <summary>Process-wide, bounded, thread-safe sender health and rotation pool.</summary>
public sealed class ProbeSenderHealthChecker(
    IProbeSenderSource source,
    IEmailNormalizer normalizer,
    IDnsMailResolver dnsResolver,
    IProbeSenderRotationPolicy rotationPolicy,
    IProbeSenderJitter jitter,
    IProbeSenderAffinityStore affinityStore,
    TimeProvider timeProvider,
    IOptions<EmailValidationOptions> options,
    ILogger<ProbeSenderHealthChecker> logger) : IProbeSenderHealthChecker, IProbeSenderPool, IDisposable
{
    private readonly ProbeSenderSourceOptions _sourceOptions = options.Value.ProbeSenderSource;
    private readonly ProbeSenderRotationOptions _rotationOptions = options.Value.ProbeSenderRotation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, SenderState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentQueue = new();
    private readonly HashSet<string> _recent = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeSender;
    private string? _previousSenderForChange;
    private string? _pendingChangeReason;
    private DateTimeOffset _lastRefreshAttempt;
    private DateTimeOffset _lastSuccessfulRefresh;
    private int _activeValidationCount;
    private int _activeMailFromSuccessCount;
    private int _activeCompletedCount;
    private DateTimeOffset? _activeSince;
    private int _activeValidationThreshold;
    private int _lastCandidatesRetrieved;
    private int _invalidCandidates;
    private long _poolRefreshes;
    private long _rotations;
    private long _scheduledRotations;
    private long _failureRotations;
    private long _cooldowns;
    private long _retirements;
    private long _exhaustions;
    private TimeSpan _lastQueryDuration;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_states.Count == 0)
                await RefreshUnderLockAsync(force: false, cancellationToken);
            await EnsureActiveUnderLockAsync(ProbeSenderContext.Empty, "initial sender selected", cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProbeSenderHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_activeSender is not null && _states.TryGetValue(_activeSender, out var active) && active.Health is not null)
                return active.Health;
            return new(ProbeSenderHealthStatus.NotConfigured, null, null,
                "No usable SMTP probe sender is available from Elasticsearch.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProbeSenderSelection?> GetSenderAsync(
        ProbeSenderContext context,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            RestoreExpiredCooldowns(now);
            if (_states.Count == 0 || UsableCount(now) < _sourceOptions.RefreshThreshold ||
                now - _lastSuccessfulRefresh >= TimeSpan.FromMinutes(_sourceOptions.StaleAfterMinutes))
                await RefreshUnderLockAsync(force: false, cancellationToken);

            if (!string.IsNullOrWhiteSpace(context.PreferredSender) &&
                !context.ExcludedSenders.Contains(context.PreferredSender) &&
                _states.TryGetValue(context.PreferredSender, out var preferred) &&
                preferred.Health?.IsOperational == true && preferred.HealthExpiresAt > now &&
                preferred.State is ProbeSenderCandidateState.Active or ProbeSenderCandidateState.Healthy)
            {
                preferred.FirstUsedAt ??= now;
                preferred.LastUsedAt = now;
                preferred.ValidationCount++;
                return new(preferred.Address, preferred.State);
            }

            var active = ActiveState();
            if (active is not null && !context.ExcludedSenders.Contains(active.Address))
            {
                var decision = rotationPolicy.Evaluate(
                    active.ToStatistics(
                        _activeValidationCount,
                        _activeCompletedCount,
                        _activeMailFromSuccessCount,
                        _activeSince),
                    _activeValidationThreshold,
                    now,
                    HasAlternate(context, now));
                if (decision.ShouldRotate)
                {
                    active.State = ProbeSenderCandidateState.Healthy;
                    Remember(active.Address);
                    _previousSenderForChange = active.Address;
                    _activeSender = null;
                    _pendingChangeReason = decision.Reason;
                    _scheduledRotations++;
                }
            }
            else if (active is not null)
            {
                active.State = ProbeSenderCandidateState.Healthy;
                _previousSenderForChange = active.Address;
                _activeSender = null;
                _pendingChangeReason ??= "alternate sender requested within the existing attempt budget";
            }

            await EnsureActiveUnderLockAsync(context, _pendingChangeReason ?? "sender selected", cancellationToken);
            active = ActiveState();
            if (active is null)
            {
                await RefreshUnderLockAsync(force: false, cancellationToken);
                await EnsureActiveUnderLockAsync(context, _pendingChangeReason ?? "pool refreshed", cancellationToken);
                active = ActiveState();
            }
            if (active is null)
            {
                _exhaustions++;
                logger.LogWarning("No usable SMTP probe sender is available. Mailbox verification unavailable.");
                return null;
            }

            var usedAt = timeProvider.GetUtcNow();
            active.FirstUsedAt ??= usedAt;
            active.LastUsedAt = usedAt;
            active.ValidationCount++;
            _activeValidationCount++;
            return new(active.Address, active.State);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordOutcomeAsync(ProbeSenderOutcome outcome, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_states.TryGetValue(outcome.Sender, out var state)) return;
            if (outcome.FailureScope == ValidationFailureScope.Sender &&
                outcome.RecipientDomain is not null && !outcome.SenderGloballyInvalid)
            {
                state.SenderFailureCount++;
                state.ConsecutiveSenderFailures++;
                // A remote domain rejecting this identity is compatibility evidence,
                // not proof that the sender is globally invalid.
                return;
            }
            switch (outcome.Kind)
            {
                case ProbeSenderOutcomeKind.MailFromAccepted:
                case ProbeSenderOutcomeKind.RecipientOutcome:
                    state.MailFromSuccessCount++;
                    if (string.Equals(_activeSender, state.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        _activeCompletedCount++;
                        _activeMailFromSuccessCount++;
                    }
                    state.ConsecutiveSenderFailures = 0;
                    break;
                case ProbeSenderOutcomeKind.SenderInvalid:
                    if (string.Equals(_activeSender, state.Address, StringComparison.OrdinalIgnoreCase))
                        _activeCompletedCount++;
                    state.SenderFailureCount++;
                    state.ConsecutiveSenderFailures++;
                    state.State = ProbeSenderCandidateState.Retired;
                    Remember(state.Address);
                    _retirements++;
                    logger.LogWarning("Probe sender retired as invalid: {ProbeSender}", state.Address);
                    affinityStore.RemoveSender(state.Address);
                    RetireActive(state.Address, "previous sender failed MAIL FROM validation");
                    _states.Remove(state.Address);
                    break;
                case ProbeSenderOutcomeKind.SenderTemporaryFailure:
                    if (string.Equals(_activeSender, state.Address, StringComparison.OrdinalIgnoreCase))
                        _activeCompletedCount++;
                    state.SenderFailureCount++;
                    state.ConsecutiveSenderFailures++;
                    state.State = ProbeSenderCandidateState.CoolingDown;
                    state.CooldownUntil = timeProvider.GetUtcNow().AddSeconds(_rotationOptions.SenderCooldownSeconds);
                    _cooldowns++;
                    logger.LogWarning("Probe sender entered cooldown until {CooldownUntil}: {ProbeSender}", state.CooldownUntil, state.Address);
                    RetireActive(state.Address, "previous sender entered cooldown after a temporary MAIL FROM failure");
                    break;
                case ProbeSenderOutcomeKind.ProviderRestriction:
                case ProbeSenderOutcomeKind.Inconclusive:
                default:
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ProbeSenderPoolSnapshot GetSnapshot()
    {
        _gate.Wait();
        try
        {
            return new(
                _sourceOptions.Provider,
                _sourceOptions.Index,
                _sourceOptions.QueryLimit,
                _lastCandidatesRetrieved,
                UsableCount(timeProvider.GetUtcNow()),
                _invalidCandidates,
                _activeSender,
                _poolRefreshes,
                _rotations,
                _scheduledRotations,
                _failureRotations,
                _cooldowns,
                _retirements,
                _exhaustions,
                _lastQueryDuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshUnderLockAsync(bool force, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!force && now - _lastRefreshAttempt < TimeSpan.FromSeconds(_sourceOptions.RefreshIntervalSeconds)) return;
        _lastRefreshAttempt = now;
        var watch = Stopwatch.StartNew();
        try
        {
            var fetched = await source.GetCandidatesAsync(_sourceOptions.QueryLimit, cancellationToken);
            watch.Stop();
            if (source is IProbeSenderSourceDiagnostics diagnostics)
            {
                _lastQueryDuration = diagnostics.LastQueryDuration;
                _lastCandidatesRetrieved = diagnostics.LastRetrievedCount;
                _invalidCandidates += diagnostics.LastInvalidCount;
            }
            else
            {
                _lastQueryDuration = watch.Elapsed;
                _lastCandidatesRetrieved = fetched.Count;
            }
            _poolRefreshes++;
            _lastSuccessfulRefresh = now;
            foreach (var candidate in fetched)
            {
                if (_states.Count >= _sourceOptions.QueryLimit) break;
                if (_states.ContainsKey(candidate.Address) || _recent.Contains(candidate.Address)) continue;
                var normalized = normalizer.Normalize(candidate.Address);
                if (!normalized.IsValid || normalized.NormalizedEmail is null)
                {
                    _invalidCandidates++;
                    continue;
                }
                _states[normalized.NormalizedEmail] = new SenderState(normalized.NormalizedEmail, candidate.LoadedAt);
            }
            logger.LogInformation(
                "Probe sender pool loaded: {UsableCandidates} usable candidates ({CandidatesRetrieved} retrieved)",
                UsableCount(now),
                _lastCandidatesRetrieved);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            watch.Stop();
            _lastQueryDuration = watch.Elapsed;
            if (_states.Count > 0)
                logger.LogWarning(exception, "Probe sender pool refresh failed; continuing with existing sender pool.");
            else
                logger.LogWarning(exception, "Probe sender pool refresh failed; no usable SMTP probe sender is available.");
        }
    }

    private async Task EnsureActiveUnderLockAsync(
        ProbeSenderContext context,
        string reason,
        CancellationToken cancellationToken)
    {
        if (ActiveState() is not null) return;
        var now = timeProvider.GetUtcNow();
        var ordered = _states.Values
            .Where(state => IsSelectable(state, context, now))
            .OrderBy(state => _recent.Contains(state.Address))
            .ThenBy(state => state.ValidationCount)
            .ThenBy(state => state.LastUsedAt)
            .ToArray();
        foreach (var candidate in ordered)
        {
            if (candidate.Health is null || candidate.HealthExpiresAt <= now)
            {
                candidate.Health = await EvaluateAsync(candidate.Address, cancellationToken);
                candidate.HealthExpiresAt = now.AddMinutes(Math.Max(1, options.Value.Smtp.ProbeSenderHealthCacheMinutes));
                if (!candidate.Health.IsOperational)
                {
                    _invalidCandidates++;
                    if (candidate.Health.Status == ProbeSenderHealthStatus.DnsUnavailable)
                        candidate.State = ProbeSenderCandidateState.Degraded;
                    else
                    {
                        candidate.State = ProbeSenderCandidateState.Invalid;
                        Remember(candidate.Address);
                        affinityStore.RemoveSender(candidate.Address);
                        _states.Remove(candidate.Address);
                    }
                    continue;
                }
                candidate.State = ProbeSenderCandidateState.Healthy;
            }
            if (!candidate.Health.IsOperational) continue;
            if (candidate.State != ProbeSenderCandidateState.Healthy) continue;
            var previous = _previousSenderForChange;
            candidate.State = ProbeSenderCandidateState.Active;
            _activeSender = candidate.Address;
            _previousSenderForChange = null;
            _activeValidationCount = 0;
            _activeMailFromSuccessCount = 0;
            _activeCompletedCount = 0;
            _activeSince = now;
            _activeValidationThreshold = jitter.Apply(
                _rotationOptions.MaxValidationsPerSender,
                _rotationOptions.JitterPercent);
            _pendingChangeReason = null;
            if (previous is null)
                logger.LogInformation("Active probe sender: {ProbeSender}", candidate.Address);
            else
            {
                _rotations++;
                logger.LogInformation(
                    "Probe sender changed: {PreviousSender} -> {ProbeSender}. Reason: {Reason}",
                    previous ?? "none",
                    candidate.Address,
                    reason);
            }
            return;
        }
    }

    private async Task<ProbeSenderHealth> EvaluateAsync(string sender, CancellationToken cancellationToken)
    {
        var normalized = normalizer.Normalize(sender);
        if (!normalized.IsValid || normalized.Domain is null)
            return new(ProbeSenderHealthStatus.InvalidSyntax, sender, null, "The SMTP probe sender has invalid syntax.");
        var domain = normalized.Domain;
        if (domain.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(domain, "example.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(domain, "example.org", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(domain, "example.net", StringComparison.OrdinalIgnoreCase))
            return new(ProbeSenderHealthStatus.NoMailRouting, normalized.NormalizedEmail, domain,
                "The SMTP probe sender uses a reserved or placeholder domain.");
        var dns = await dnsResolver.ResolveAsync(domain, cancellationToken);
        if (dns.Status == DnsStatus.DomainNotFound || !dns.DomainExists)
            return new(ProbeSenderHealthStatus.DomainNotFound, normalized.NormalizedEmail, domain, "The sender domain does not exist.");
        if (dns.Status is DnsStatus.Timeout or DnsStatus.Failure)
            return new(ProbeSenderHealthStatus.DnsUnavailable, normalized.NormalizedEmail, domain, "The sender domain could not be checked reliably.");
        if (dns.ExplicitNullMx || !dns.MxPresent)
            return new(ProbeSenderHealthStatus.NoMailRouting, normalized.NormalizedEmail, domain, "The sender domain has no usable mail route.");
        return new(ProbeSenderHealthStatus.Valid, normalized.NormalizedEmail, domain, "The sender has valid syntax and a usable DNS mail route.");
    }

    private SenderState? ActiveState() =>
        _activeSender is not null && _states.TryGetValue(_activeSender, out var state) &&
        state.State == ProbeSenderCandidateState.Active ? state : null;

    private bool HasAlternate(ProbeSenderContext context, DateTimeOffset now) =>
        _states.Values.Any(state => state.Address != _activeSender && IsSelectable(state, context, now));

    private static bool IsSelectable(SenderState state, ProbeSenderContext context, DateTimeOffset now) =>
        !context.ExcludedSenders.Contains(state.Address) &&
        (state.State is ProbeSenderCandidateState.Candidate or ProbeSenderCandidateState.Healthy ||
         state.State == ProbeSenderCandidateState.CoolingDown && state.CooldownUntil <= now ||
         state.State == ProbeSenderCandidateState.Degraded && state.HealthExpiresAt <= now);

    private int UsableCount(DateTimeOffset now) => _states.Values.Count(state =>
        state.State is ProbeSenderCandidateState.Candidate or ProbeSenderCandidateState.Healthy or ProbeSenderCandidateState.Active ||
        state.State == ProbeSenderCandidateState.CoolingDown && state.CooldownUntil <= now);

    private void RestoreExpiredCooldowns(DateTimeOffset now)
    {
        foreach (var state in _states.Values.Where(state =>
                     state.State == ProbeSenderCandidateState.CoolingDown && state.CooldownUntil <= now))
            state.State = ProbeSenderCandidateState.Healthy;
    }

    private void RetireActive(string sender, string reason)
    {
        if (!string.Equals(_activeSender, sender, StringComparison.OrdinalIgnoreCase)) return;
        _previousSenderForChange = sender;
        _activeSender = null;
        _pendingChangeReason = reason;
        _failureRotations++;
    }

    private void Remember(string sender)
    {
        if (!_recent.Add(sender)) return;
        _recentQueue.Enqueue(sender);
        while (_recentQueue.Count > _sourceOptions.RecentlyUsedLimit)
            _recent.Remove(_recentQueue.Dequeue());
    }

    public void Dispose() => _gate.Dispose();

    private sealed class SenderState(string address, DateTimeOffset loadedAt)
    {
        public string Address { get; } = address;
        public ProbeSenderCandidateState State { get; set; } = ProbeSenderCandidateState.Candidate;
        public DateTimeOffset LoadedAt { get; } = loadedAt;
        public DateTimeOffset? FirstUsedAt { get; set; }
        public DateTimeOffset? LastUsedAt { get; set; }
        public int ValidationCount { get; set; }
        public int MailFromSuccessCount { get; set; }
        public int SenderFailureCount { get; set; }
        public int ConsecutiveSenderFailures { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }
        public ProbeSenderHealth? Health { get; set; }
        public DateTimeOffset HealthExpiresAt { get; set; }

        public ProbeSenderRuntimeStatistics ToStatistics(
            int activeValidationCount,
            int activeCompletedCount,
            int activeMailFromSuccessCount,
            DateTimeOffset? activeSince) => new(
            Address, State, LoadedAt, FirstUsedAt, LastUsedAt, ValidationCount,
            activeValidationCount, activeCompletedCount, activeMailFromSuccessCount,
            SenderFailureCount, ConsecutiveSenderFailures, CooldownUntil, activeSince);
    }
}

public sealed class ProbeSenderJitter : IProbeSenderJitter
{
    public int Apply(int target, int percent)
    {
        var boundedTarget = Math.Max(1, target);
        var spread = boundedTarget * Math.Clamp(percent, 0, 50) / 100;
        return spread == 0 ? boundedTarget : Random.Shared.Next(boundedTarget - spread, boundedTarget + spread + 1);
    }
}

internal static class SmtpSenderFailureClassifier
{
    private static readonly string[] SenderMarkers =
        ["sender", "mail from", "from address", "return path", "return-path"];
    private static readonly string[] SourceOrProviderMarkers =
        ["rate limit", "too many", "throttl", "source ip", "your ip", "ip address", "blacklist", "spamhaus", "reputation", "anti-abuse", "reverse dns", "forward-confirmed"];
    private static readonly string[] GenericProviderPolicyMarkers =
        ["access denied", "blocked by policy", "rejected by policy", "authentication required", "relay denied", "relaying denied", "unable to relay"];

    internal static ProbeSenderOutcomeKind Classify(SmtpProbeResult result)
    {
        var session = result.SessionEvidence;
        var evidence = result.Evidence;
        if (session?.MailFromSucceeded == true)
            return session.RecipientStageReached ? ProbeSenderOutcomeKind.RecipientOutcome : ProbeSenderOutcomeKind.MailFromAccepted;
        if (session?.FailedStage != SmtpCommand.MailFrom && evidence?.Command != SmtpCommand.MailFrom)
            return ProbeSenderOutcomeKind.Inconclusive;
        if (evidence?.Category == SmtpResponseCategory.RateLimited)
            return ProbeSenderOutcomeKind.ProviderRestriction;
        var response = evidence?.SanitizedResponse ?? result.Response ?? string.Empty;
        if (SourceOrProviderMarkers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return ProbeSenderOutcomeKind.ProviderRestriction;
        var explicitlySenderSpecific = SenderMarkers.Any(marker =>
            response.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (!explicitlySenderSpecific &&
            (GenericProviderPolicyMarkers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
             evidence?.TextClassification is SmtpResponseTextClassification.AntiAbuse or
                 SmtpResponseTextClassification.RateLimit or
                 SmtpResponseTextClassification.RelayDenied or
                 SmtpResponseTextClassification.VerificationUnavailable))
            return ProbeSenderOutcomeKind.ProviderRestriction;
        if (evidence?.ResponseCode is >= 500 and < 600)
            return ProbeSenderOutcomeKind.SenderInvalid;
        if (explicitlySenderSpecific && (evidence?.ResponseCode is >= 400 and < 500 ||
            evidence?.Category is SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted)
           )
            return ProbeSenderOutcomeKind.SenderTemporaryFailure;
        return ProbeSenderOutcomeKind.Inconclusive;
    }

    internal static bool ShouldTryAlternate(SmtpProbeResult result) =>
        Classify(result) is ProbeSenderOutcomeKind.SenderInvalid or ProbeSenderOutcomeKind.SenderTemporaryFailure;

    internal static ValidationFailureScope Scope(SmtpProbeResult result)
    {
        var outcome = Classify(result);
        if (outcome is ProbeSenderOutcomeKind.SenderInvalid or ProbeSenderOutcomeKind.SenderTemporaryFailure)
            return ValidationFailureScope.Sender;
        if (outcome == ProbeSenderOutcomeKind.RecipientOutcome)
            return ValidationFailureScope.Recipient;
        if (outcome == ProbeSenderOutcomeKind.ProviderRestriction)
            return result.Response?.Contains("source ip", StringComparison.OrdinalIgnoreCase) == true ||
                   result.Response?.Contains("your ip", StringComparison.OrdinalIgnoreCase) == true
                ? ValidationFailureScope.SourceIp
                : ValidationFailureScope.Provider;
        return result.Evidence?.Category is SmtpResponseCategory.ConnectionRejected or SmtpResponseCategory.Timeout
            ? ValidationFailureScope.Connection
            : ValidationFailureScope.Unknown;
    }
}
