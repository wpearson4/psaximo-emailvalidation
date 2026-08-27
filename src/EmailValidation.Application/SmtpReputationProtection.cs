using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Application;

public sealed class SmtpReputationPolicy(IOptions<EmailValidationOptions> options)
{
    private readonly SmtpReputationProtectionOptions _options = options.Value.SmtpReputationProtection;

    public SmtpReputationEvidence Evaluate(
        SmtpReputationBudgetContext context,
        IReadOnlyList<SmtpReputationScopeSnapshot> states,
        DateTimeOffset now)
    {
        if (!_options.Enabled || _options.Mode == SmtpReputationProtectionMode.Disabled)
            return new SmtpReputationEvidence
            {
                Decision = SmtpProbeBudgetDecision.Allow,
                WouldDecision = SmtpProbeBudgetDecision.Allow,
                Mode = _options.Mode,
                CircuitState = SmtpReputationState.Disabled,
                WouldHaveUsedIdentityId = context.OutboundIdentityId,
                EvaluatedAtUtc = now,
                PolicyVersion = _options.PolicyVersion
            };

        var restrictions = new List<(SmtpProbeBudgetDecision Decision, SmtpReputationScopeSnapshot State,
            DateTimeOffset? RetryAt, string Reason)>();
        foreach (var state in states)
        {
            if ((state.State is SmtpReputationState.CircuitOpen or SmtpReputationState.Cooldown) &&
                state.CooldownUntilUtc > now)
                restrictions.Add((SmtpProbeBudgetDecision.CircuitOpen, state, state.CooldownUntilUtc,
                    $"{state.ScopeType}CircuitOpen"));
            else if (state.State == SmtpReputationState.HalfOpen &&
                     state.HalfOpenProbeCount >= Math.Max(1, _options.CircuitBreaker.HalfOpenMaximumProbes))
                restrictions.Add((SmtpProbeBudgetDecision.DeferToDurableRetry, state,
                    now.AddMinutes(Math.Max(1, _options.CircuitBreaker.CooldownMinutes)),
                    $"{state.ScopeType}HalfOpenBudgetExhausted"));
        }

        var mailbox = states.FirstOrDefault(item => item.ScopeType == SmtpReputationScopeType.Mailbox);
        if (mailbox is not null)
        {
            var minimumNext = mailbox.LastLiveSmtpAttemptAtUtc?.AddMinutes(
                Math.Max(0, _options.Mailbox.MinimumMinutesBetweenLiveProbes));
            if (minimumNext > now)
                restrictions.Add((SmtpProbeBudgetDecision.Delay, mailbox, minimumNext,
                    "MailboxMinimumProbeInterval"));
            if (mailbox.ConnectionCount >= Math.Max(1, _options.Mailbox.MaximumLiveProbesPer24Hours))
                restrictions.Add((SmtpProbeBudgetDecision.DeferToDurableRetry, mailbox,
                    mailbox.WindowStartedAtUtc.AddHours(24), "MailboxDailyProbeBudget"));
        }

        var restriction = restrictions
            .OrderByDescending(item => Severity(item.Decision))
            .ThenByDescending(item => item.RetryAt)
            .FirstOrDefault();
        var would = restriction.State is null ? SmtpProbeBudgetDecision.Allow : restriction.Decision;
        var actual = _options.Mode == SmtpReputationProtectionMode.Enforced
            ? would
            : SmtpProbeBudgetDecision.Allow;
        return Evidence(actual, would, restriction.State, restriction.RetryAt,
            states, context, now, restriction.Reason);
    }

    public SmtpReputationScopeSnapshot NormalizeWindow(
        SmtpReputationScopeSnapshot state,
        DateTimeOffset now)
    {
        var window = state.ScopeType == SmtpReputationScopeType.Mailbox
            ? TimeSpan.FromHours(24)
            : TimeSpan.FromMinutes(Math.Max(1, _options.WindowMinutes));
        var normalized = state.WindowStartedAtUtc.Add(window) <= now
            ? state with
            {
                WindowStartedAtUtc = now,
                ConnectionCount = 0,
                RcptCount = 0,
                UnknownRecipientCount = 0,
                PolicyBlockCount = 0,
                TemporaryDeferralCount = 0,
                ConnectionFailureCount = 0,
                AffectedIdentityIds = [],
                AffectedProviders = []
            }
            : state;
        if ((normalized.State is SmtpReputationState.CircuitOpen or SmtpReputationState.Cooldown) &&
            normalized.CooldownUntilUtc <= now)
            normalized = normalized with
            {
                State = SmtpReputationState.HalfOpen,
                HalfOpenProbeCount = 0,
                ConsecutiveRecoverySuccesses = 0,
                LastStateChangedAtUtc = now
            };
        return normalized;
    }

    public SmtpReputationScopeSnapshot Apply(
        SmtpReputationScopeSnapshot current,
        SmtpReputationObservation observation)
    {
        var now = observation.OccurredAtUtc;
        var state = NormalizeWindow(current, now);
        var reason = observation.NormalizedReason;
        var unknown = reason is SmtpNormalizedReason.MailboxNotFound or
            SmtpNormalizedReason.MailboxDisabled or SmtpNormalizedReason.RecipientRejected;
        var policyBlock = reason is SmtpNormalizedReason.ProviderRateLimit or
            SmtpNormalizedReason.ProviderConnectionLimit or SmtpNormalizedReason.PolicyBlock or
            SmtpNormalizedReason.IpPolicyBlock or SmtpNormalizedReason.ReputationBlocked or
            SmtpNormalizedReason.VerificationRefused;
        var temporary = observation.Category is SmtpResponseCategory.TemporaryFailure or
            SmtpResponseCategory.Greylisted or SmtpResponseCategory.RateLimited or
            SmtpResponseCategory.VerificationBlocked;
        var connectionFailure = observation.Category is SmtpResponseCategory.ConnectionRejected or
            SmtpResponseCategory.Timeout;
        var connectionIncrement = observation.ConnectionAttempted &&
            state.ScopeType != SmtpReputationScopeType.Mailbox ? 1 : 0;
        var rcptIncrement = observation.RcptAttempted ? 1 : 0;
        var identities = AddDistinct(state.AffectedIdentityIds, observation.Context.OutboundIdentityId);
        var providers = AddDistinct(state.AffectedProviders, observation.Context.Provider.ToString());
        var next = state with
        {
            ConnectionCount = state.ConnectionCount + connectionIncrement,
            RcptCount = state.RcptCount + rcptIncrement,
            UnknownRecipientCount = state.UnknownRecipientCount + (unknown && observation.RcptAttempted ? 1 : 0),
            PolicyBlockCount = state.PolicyBlockCount + (policyBlock ? 1 : 0),
            TemporaryDeferralCount = state.TemporaryDeferralCount + (temporary ? 1 : 0),
            ConnectionFailureCount = state.ConnectionFailureCount + (connectionFailure ? 1 : 0),
            AffectedIdentityIds = identities,
            AffectedProviders = providers,
            LastHealthyAtUtc = IsConclusive(observation.Category) ? now : state.LastHealthyAtUtc,
            PolicyVersion = _options.PolicyVersion
        };
        return Transition(next, policyBlock, observation.Category, now);
    }

    public SmtpReputationScopeSnapshot ReserveMailbox(
        SmtpReputationScopeSnapshot state,
        DateTimeOffset now)
    {
        var normalized = NormalizeWindow(state, now);
        return normalized with
        {
            ConnectionCount = normalized.ConnectionCount + 1,
            LastLiveSmtpAttemptAtUtc = now,
            HalfOpenProbeCount = normalized.State == SmtpReputationState.HalfOpen
                ? normalized.HalfOpenProbeCount + 1
                : normalized.HalfOpenProbeCount,
            PolicyVersion = _options.PolicyVersion
        };
    }

    public SmtpReputationScopeSnapshot ReserveHalfOpen(
        SmtpReputationScopeSnapshot state,
        DateTimeOffset now)
    {
        var normalized = NormalizeWindow(state, now);
        return normalized.State == SmtpReputationState.HalfOpen
            ? normalized with { HalfOpenProbeCount = normalized.HalfOpenProbeCount + 1 }
            : normalized;
    }

    private SmtpReputationScopeSnapshot Transition(
        SmtpReputationScopeSnapshot state,
        bool policyBlock,
        SmtpResponseCategory category,
        DateTimeOffset now)
    {
        if (!_options.CircuitBreaker.Enabled || state.ScopeType == SmtpReputationScopeType.Mailbox)
            return state;
        if (state.State == SmtpReputationState.HalfOpen)
        {
            if (policyBlock || category is SmtpResponseCategory.ConnectionRejected or SmtpResponseCategory.Timeout)
                return Open(state, now);
            if (IsConclusive(category))
            {
                var successes = state.ConsecutiveRecoverySuccesses + 1;
                var halfOpenSuccesses = Math.Min(
                    Math.Max(1, _options.CircuitBreaker.RecoverySuccessesRequired),
                    Math.Max(1, _options.CircuitBreaker.HalfOpenMaximumProbes));
                return successes >= halfOpenSuccesses
                    ? state with
                    {
                        State = SmtpReputationState.Degraded,
                        ConsecutiveRecoverySuccesses = successes,
                        CooldownUntilUtc = null,
                        LastStateChangedAtUtc = now
                    }
                    : state with { ConsecutiveRecoverySuccesses = successes };
            }
        }

        var shouldOpen = state.ScopeType switch
        {
            SmtpReputationScopeType.RecipientDomain =>
                _options.UnknownRecipientPressure.Enabled &&
                state.RcptCount >= _options.UnknownRecipientPressure.MinimumRcptObservations &&
                state.UnknownRecipientRatio >= _options.UnknownRecipientPressure.OpenRatio ||
                PressureOpen(state),
            SmtpReputationScopeType.ProviderIdentity =>
                state.PolicyBlockCount >= Math.Max(1, _options.CircuitBreaker.ProviderIdentityPolicyBlockCount),
            SmtpReputationScopeType.Provider =>
                PressureOpen(state) && state.AffectedIdentityIds.Count >=
                    Math.Max(2, _options.CircuitBreaker.ProviderAffectedIdentityCount),
            SmtpReputationScopeType.NetworkBlock =>
                PressureOpen(state) &&
                state.AffectedProviders.Count >= Math.Max(2, _options.CircuitBreaker.NetworkAffectedProviderCount) &&
                state.AffectedIdentityIds.Count >= Math.Max(2, _options.CircuitBreaker.NetworkAffectedIdentityCount),
            _ => false
        };
        if (shouldOpen) return Open(state, now);
        if (_options.PolicyBlockPressure.Enabled &&
            state.ObservationCount >= _options.PolicyBlockPressure.MinimumObservations &&
            state.PolicyBlockRate >= _options.PolicyBlockPressure.DegradedRatio)
            return state.State == SmtpReputationState.Degraded
                ? state
                : state with { State = SmtpReputationState.Degraded, LastStateChangedAtUtc = now };
        if (state.State == SmtpReputationState.Degraded && IsConclusive(category))
            return state with
            {
                State = SmtpReputationState.Healthy,
                ConsecutiveRecoverySuccesses = 0,
                LastStateChangedAtUtc = now
            };
        return state;
    }

    private bool PressureOpen(SmtpReputationScopeSnapshot state) =>
        _options.PolicyBlockPressure.Enabled &&
        state.ObservationCount >= Math.Max(
            _options.CircuitBreaker.MinimumObservationsBeforeEvaluation,
            _options.PolicyBlockPressure.MinimumObservations) &&
        state.PolicyBlockRate >= _options.PolicyBlockPressure.OpenRatio;

    private SmtpReputationScopeSnapshot Open(SmtpReputationScopeSnapshot state, DateTimeOffset now) => state with
    {
        State = SmtpReputationState.CircuitOpen,
        CooldownUntilUtc = now.AddMinutes(Math.Max(1, _options.CircuitBreaker.CooldownMinutes)),
        HalfOpenProbeCount = 0,
        ConsecutiveRecoverySuccesses = 0,
        LastStateChangedAtUtc = now
    };

    private SmtpReputationEvidence Evidence(
        SmtpProbeBudgetDecision actual,
        SmtpProbeBudgetDecision would,
        SmtpReputationScopeSnapshot? restriction,
        DateTimeOffset? retryAt,
        IReadOnlyList<SmtpReputationScopeSnapshot> states,
        SmtpReputationBudgetContext context,
        DateTimeOffset now,
        string? reason = null) => new()
    {
        Decision = actual,
        WouldDecision = would,
        Mode = _options.Mode,
        RestrictingScope = restriction?.ScopeType,
        CircuitState = restriction?.State ?? SmtpReputationState.Healthy,
        RetryAtUtc = retryAt,
        SuppressionReason = reason,
        WouldHaveUsedIdentityId = context.OutboundIdentityId,
        ScopeStates = states.ToDictionary(item => item.ScopeType, item => item.State),
        MailboxProbeCount = states.FirstOrDefault(item => item.ScopeType == SmtpReputationScopeType.Mailbox)
            ?.ConnectionCount ?? 0,
        EvaluatedAtUtc = now,
        PolicyVersion = _options.PolicyVersion
    };

    private static int Severity(SmtpProbeBudgetDecision decision) => decision switch
    {
        SmtpProbeBudgetDecision.CircuitOpen => 4,
        SmtpProbeBudgetDecision.SafeFallback => 3,
        SmtpProbeBudgetDecision.DeferToDurableRetry => 2,
        SmtpProbeBudgetDecision.Delay => 1,
        _ => 0
    };

    private static IReadOnlyList<string> AddDistinct(IReadOnlyList<string> values, string? value) =>
        string.IsNullOrWhiteSpace(value) || values.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? values
            : values.Append(value).TakeLast(64).ToArray();

    private static bool IsConclusive(SmtpResponseCategory category) => category is
        SmtpResponseCategory.Accepted or SmtpResponseCategory.RecipientRejected or SmtpResponseCategory.MailboxFull;
}

public sealed class SmtpReputationProtectionService(
    ISmtpReputationStateStore store,
    SmtpReputationPolicy policy,
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider,
    ILogger<SmtpReputationProtectionService> logger) : ISmtpReputationProtection
{
    private static readonly Meter Meter = new("EmailValidation.SmtpReputation", "1.0.0");
    private static readonly Counter<long> Allowed = Meter.CreateCounter<long>("smtp_probe_allowed_total");
    private static readonly Counter<long> Deferred = Meter.CreateCounter<long>("smtp_probe_deferred_total");
    private static readonly Counter<long> ObserveWouldBlock =
        Meter.CreateCounter<long>("smtp_reputation_observe_would_block_total");
    private static readonly Counter<long> UnknownRecipients =
        Meter.CreateCounter<long>("smtp_unknown_recipient_total");
    private static readonly Counter<long> PolicyBlocks = Meter.CreateCounter<long>("smtp_policy_block_total");
    private static readonly Counter<long> ProviderRateLimits =
        Meter.CreateCounter<long>("smtp_provider_rate_limit_total");
    private static readonly Counter<long> IpPolicyBlocks =
        Meter.CreateCounter<long>("smtp_ip_policy_block_total");
    private static readonly Counter<long> Connections = Meter.CreateCounter<long>("smtp_connection_attempt_total");
    private static readonly Counter<long> RcptAttempts = Meter.CreateCounter<long>("smtp_rcpt_attempt_total");
    private static readonly Counter<long> CircuitsOpened = Meter.CreateCounter<long>("smtp_circuit_open_total");
    private static readonly Counter<long> CircuitsHalfOpened =
        Meter.CreateCounter<long>("smtp_circuit_half_open_total");
    private static readonly Counter<long> CircuitsClosed = Meter.CreateCounter<long>("smtp_circuit_closed_total");
    private static readonly Counter<long> HalfOpenProbes =
        Meter.CreateCounter<long>("smtp_reputation_half_open_probe_total");
    private static readonly Counter<long> HalfOpenSuccesses =
        Meter.CreateCounter<long>("smtp_reputation_half_open_success_total");
    private static readonly Counter<long> HalfOpenFailures =
        Meter.CreateCounter<long>("smtp_reputation_half_open_failure_total");
    private readonly SmtpReputationProtectionOptions _options = options.Value.SmtpReputationProtection;
    private readonly ConcurrentDictionary<string, SmtpReputationScopeSnapshot> _local = new(StringComparer.Ordinal);

    public async Task<SmtpReputationEvidence> EvaluateAsync(
        SmtpReputationBudgetContext context,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (!_options.Enabled || _options.Mode == SmtpReputationProtectionMode.Disabled)
            return policy.Evaluate(context, [], now);
        var scopeKeys = ScopeKeys(context);
        IReadOnlyList<SmtpReputationScopeSnapshot> states;
        try
        {
            states = await store.GetManyAsync(scopeKeys, cancellationToken).ConfigureAwait(false);
            states = scopeKeys.Select(scope =>
            {
                var remote = states.FirstOrDefault(item => item.ScopeType == scope.ScopeType &&
                    string.Equals(item.ScopeId, scope.ScopeId, StringComparison.Ordinal));
                return _local.TryGetValue(Key(scope.ScopeType, scope.ScopeId), out var local) &&
                       (remote is null || local.Version > remote.Version)
                    ? local
                    : remote;
            }).Where(state => state is not null).Cast<SmtpReputationScopeSnapshot>().ToArray();
            foreach (var state in states) _local[Key(state.ScopeType, state.ScopeId)] = state;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "SMTP reputation state query failed; applying conservative fallback");
            states = scopeKeys.Select(scope => _local.TryGetValue(Key(scope.ScopeType, scope.ScopeId), out var state)
                ? state
                : New(scope.ScopeType, scope.ScopeId, context.Provider, now)).ToArray();
            if (_options.Mode == SmtpReputationProtectionMode.Enforced && states.All(state => state.Version == 0))
            {
                var fallback = SafeFallback(context, states, now);
                RecordDecision(fallback, context.Provider);
                return fallback;
            }
        }

        states = scopeKeys.Select(scope => states.FirstOrDefault(item =>
                item.ScopeType == scope.ScopeType && string.Equals(item.ScopeId, scope.ScopeId, StringComparison.Ordinal))
            ?? New(scope.ScopeType, scope.ScopeId, context.Provider, now)).Select(state => policy.NormalizeWindow(state, now)).ToArray();
        var evidence = policy.Evaluate(context, states, now);
        if (!evidence.SuppressSmtp && context.ReserveMailboxProbe)
        {
            var reserved = new List<SmtpReputationScopeSnapshot>(states.Count);
            foreach (var state in states.OrderBy(item =>
                         item.ScopeType == SmtpReputationScopeType.Mailbox ? 0 : 1))
            {
                SmtpReputationScopeSnapshot candidate;
                try
                {
                    var reservation = await ReserveScopeAsync(
                        state.ScopeType, state.ScopeId, context, now, cancellationToken).ConfigureAwait(false);
                    if (reservation.Restriction?.SuppressSmtp == true)
                    {
                        RecordDecision(reservation.Restriction, context.Provider);
                        return reservation.Restriction;
                    }
                    candidate = reservation.State;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception,
                        "SMTP reputation probe reservation persistence failed for {ScopeType}; using conservative local state",
                        state.ScopeType);
                    candidate = state.ScopeType == SmtpReputationScopeType.Mailbox
                        ? policy.ReserveMailbox(state, now)
                        : policy.ReserveHalfOpen(state, now);
                    candidate = candidate with { Version = state.Version + 1 };
                    _local[Key(candidate.ScopeType, candidate.ScopeId)] = candidate;
                    RecordStateTransition(state, candidate, context.Provider);
                    if (_options.Mode == SmtpReputationProtectionMode.Enforced)
                    {
                        var fallback = SafeFallback(context, states, now);
                        RecordDecision(fallback, context.Provider);
                        return fallback;
                    }
                }
                reserved.Add(candidate);
            }
            evidence = evidence with
            {
                ScopeStates = reserved.ToDictionary(item => item.ScopeType, item => item.State),
                MailboxProbeCount = reserved.First(item =>
                    item.ScopeType == SmtpReputationScopeType.Mailbox).ConnectionCount
            };
        }
        RecordDecision(evidence, context.Provider);
        return evidence;
    }

    private async Task<(SmtpReputationScopeSnapshot State, SmtpReputationEvidence? Restriction)>
        ReserveScopeAsync(
            SmtpReputationScopeType scopeType,
            string scopeId,
            SmtpReputationBudgetContext context,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var fetched = await store.GetManyAsync([(scopeType, scopeId)], cancellationToken).ConfigureAwait(false);
            var current = fetched.Count == 0
                ? New(scopeType, scopeId, context.Provider, now)
                : fetched[0];
            if (_local.TryGetValue(Key(scopeType, scopeId), out var local) && local.Version > current.Version)
                current = local with { Version = current.Version };
            var before = current;
            current = policy.NormalizeWindow(current, now);
            var restriction = policy.Evaluate(context, [current], now);
            if (restriction.SuppressSmtp)
                return (current, restriction);
            if (scopeType != SmtpReputationScopeType.Mailbox &&
                current.State != SmtpReputationState.HalfOpen)
                return (current, null);
            var next = (scopeType == SmtpReputationScopeType.Mailbox
                ? policy.ReserveMailbox(current, now)
                : policy.ReserveHalfOpen(current, now)) with { Version = current.Version + 1 };
            var saved = await store.TrySaveAsync(next, current.Version, cancellationToken).ConfigureAwait(false);
            if (saved.Applied)
            {
                var applied = saved.State ?? next;
                _local[Key(applied.ScopeType, applied.ScopeId)] = applied;
                RecordStateTransition(before, applied, context.Provider);
                return (applied, null);
            }
        }
        throw new InvalidOperationException("SMTP reputation probe could not be reserved after concurrent writes.");
    }

    public async Task RecordAsync(
        SmtpReputationObservation observation,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _options.Mode == SmtpReputationProtectionMode.Disabled) return;
        if (observation.ConnectionAttempted) Connections.Add(1, ProviderTag(observation.Context.Provider));
        if (observation.RcptAttempted) RcptAttempts.Add(1, ProviderTag(observation.Context.Provider));
        if (observation.RcptAttempted && observation.NormalizedReason is SmtpNormalizedReason.MailboxNotFound or
            SmtpNormalizedReason.MailboxDisabled or SmtpNormalizedReason.RecipientRejected)
            UnknownRecipients.Add(1, ProviderTag(observation.Context.Provider));
        if (observation.NormalizedReason is SmtpNormalizedReason.ProviderRateLimit or
            SmtpNormalizedReason.ProviderConnectionLimit or SmtpNormalizedReason.PolicyBlock or
            SmtpNormalizedReason.IpPolicyBlock or SmtpNormalizedReason.ReputationBlocked or
            SmtpNormalizedReason.VerificationRefused)
            PolicyBlocks.Add(1, ProviderTag(observation.Context.Provider));
        if (observation.NormalizedReason == SmtpNormalizedReason.ProviderRateLimit)
            ProviderRateLimits.Add(1, ProviderTag(observation.Context.Provider));
        if (observation.NormalizedReason == SmtpNormalizedReason.IpPolicyBlock)
            IpPolicyBlocks.Add(1, ProviderTag(observation.Context.Provider));

        foreach (var scope in ScopeKeys(observation.Context))
        {
            try
            {
                await UpdateAsync(
                    scope.ScopeType, scope.ScopeId, observation.Context.Provider, observation.OccurredAtUtc,
                    current => policy.Apply(current, observation), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "SMTP reputation observation persistence failed for {ScopeType}; local protection remains active",
                    scope.ScopeType);
                var key = Key(scope.ScopeType, scope.ScopeId);
                var current = _local.GetOrAdd(key,
                    _ => New(scope.ScopeType, scope.ScopeId, observation.Context.Provider, observation.OccurredAtUtc));
                var next = policy.Apply(current, observation) with { Version = current.Version + 1 };
                _local[key] = next;
                RecordStateTransition(current, next, observation.Context.Provider);
            }
        }
    }

    private async Task<SmtpReputationScopeSnapshot> UpdateAsync(
        SmtpReputationScopeType scopeType,
        string scopeId,
        MailProvider provider,
        DateTimeOffset now,
        Func<SmtpReputationScopeSnapshot, SmtpReputationScopeSnapshot> update,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var fetched = await store.GetManyAsync([(scopeType, scopeId)], cancellationToken).ConfigureAwait(false);
            var current = fetched.Count == 0 ? New(scopeType, scopeId, provider, now) : fetched[0];
            if (_local.TryGetValue(Key(scopeType, scopeId), out var local) && local.Version > current.Version)
                current = local with { Version = current.Version };
            var next = update(current) with { Version = current.Version + 1 };
            var saved = await store.TrySaveAsync(next, current.Version, cancellationToken).ConfigureAwait(false);
            if (saved.Applied)
            {
                var applied = saved.State ?? next;
                _local[Key(next.ScopeType, next.ScopeId)] = applied;
                RecordStateTransition(current, applied, provider);
                return applied;
            }
        }
        throw new InvalidOperationException("SMTP reputation state could not be updated after concurrent writes.");
    }

    private List<(SmtpReputationScopeType ScopeType, string ScopeId)> ScopeKeys(
        SmtpReputationBudgetContext context)
    {
        var provider = context.Provider.ToString();
        var result = new List<(SmtpReputationScopeType ScopeType, string ScopeId)>
        {
            (SmtpReputationScopeType.NetworkBlock, _options.NetworkBlock.Trim().ToLowerInvariant()),
            (SmtpReputationScopeType.Provider, provider),
            (SmtpReputationScopeType.RecipientDomain, context.RecipientDomain.Trim().ToLowerInvariant()),
            (SmtpReputationScopeType.Mailbox, context.NormalizedMailbox.Trim().ToLowerInvariant())
        };
        if (!string.IsNullOrWhiteSpace(context.OutboundIdentityId))
            result.Insert(2, (SmtpReputationScopeType.ProviderIdentity,
                $"{provider}|{context.OutboundIdentityId.Trim().ToLowerInvariant()}"));
        return result;
    }

    private SmtpReputationScopeSnapshot New(
        SmtpReputationScopeType type,
        string id,
        MailProvider provider,
        DateTimeOffset now) => new()
    {
        ScopeType = type,
        ScopeId = id,
        Provider = provider,
        WindowStartedAtUtc = now,
        LastHealthyAtUtc = now,
        PolicyVersion = _options.PolicyVersion
    };

    private SmtpReputationEvidence SafeFallback(
        SmtpReputationBudgetContext context,
        IReadOnlyList<SmtpReputationScopeSnapshot> states,
        DateTimeOffset now) => new()
    {
        Decision = SmtpProbeBudgetDecision.SafeFallback,
        WouldDecision = SmtpProbeBudgetDecision.SafeFallback,
        Mode = _options.Mode,
        CircuitState = SmtpReputationState.Degraded,
        RetryAtUtc = now.AddMinutes(Math.Max(1, _options.FailureFallbackMinutes)),
        SuppressionReason = "ReputationStateUnavailable",
        WouldHaveUsedIdentityId = context.OutboundIdentityId,
        ScopeStates = states.ToDictionary(item => item.ScopeType, item => item.State),
        EvaluatedAtUtc = now,
        PolicyVersion = _options.PolicyVersion
    };

    private static void RecordDecision(SmtpReputationEvidence evidence, MailProvider provider)
    {
        if (evidence.Decision == SmtpProbeBudgetDecision.Allow) Allowed.Add(1, ProviderTag(provider));
        else Deferred.Add(1, ProviderTag(provider));
        if (evidence.Mode == SmtpReputationProtectionMode.Observe &&
            evidence.WouldDecision != SmtpProbeBudgetDecision.Allow)
            ObserveWouldBlock.Add(1,
                ProviderTag(provider), new("decision", evidence.WouldDecision.ToString()));
    }

    private static void RecordStateTransition(
        SmtpReputationScopeSnapshot before,
        SmtpReputationScopeSnapshot after,
        MailProvider provider)
    {
        var tags = new[]
        {
            ProviderTag(provider),
            new KeyValuePair<string, object?>("scope_type", after.ScopeType.ToString())
        };
        if (after.HalfOpenProbeCount > before.HalfOpenProbeCount)
            HalfOpenProbes.Add(1, tags);
        if (before.State == SmtpReputationState.HalfOpen &&
            after.ConsecutiveRecoverySuccesses > before.ConsecutiveRecoverySuccesses)
            HalfOpenSuccesses.Add(1, tags);
        if (before.State == SmtpReputationState.HalfOpen &&
            after.State == SmtpReputationState.CircuitOpen)
            HalfOpenFailures.Add(1, tags);
        if (before.State == after.State) return;
        if (after.State == SmtpReputationState.CircuitOpen) CircuitsOpened.Add(1, tags);
        else if (after.State == SmtpReputationState.HalfOpen) CircuitsHalfOpened.Add(1, tags);
        else if (after.State == SmtpReputationState.Healthy) CircuitsClosed.Add(1, tags);
    }

    private static string Key(SmtpReputationScopeType type, string id) => $"{type}|{id}";
    private static KeyValuePair<string, object?> ProviderTag(MailProvider provider) => new("provider", provider.ToString());
}
