using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

/// <summary>
/// Process-local domain/provider scheduler. Pacing waits occur before the global
/// lease is acquired, so a cooling domain never stalls unrelated domains.
/// </summary>
public sealed class DomainSmtpProbeThrottle : ISmtpProbeThrottle, IDisposable
{
    private readonly SmtpOptions _legacy;
    private readonly SchedulingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IDomainPacingJitter _jitter;
    private readonly IDomainBackoffPolicy _backoff;
    private readonly IProviderPolicyResolver _policyResolver;
    private readonly ILogger<DomainSmtpProbeThrottle> _logger;
    private readonly Meter _meter;
    private readonly Counter<long> _policyBlockMetric;
    private readonly Counter<long> _cooldownActivationMetric;
    private readonly Histogram<double> _cooldownDurationMetric;
    private readonly Counter<long> _halfOpenMetric;
    private readonly Counter<long> _resumptionMetric;
    private readonly Counter<long> _concurrencyWaitMetric;
    private readonly Counter<long> _pacingWaitMetric;
    private readonly Counter<long> _retryMetric;
    private readonly Counter<long> _retryExhaustionMetric;
    private readonly SemaphoreSlim _globalGate;
    private readonly ConcurrentDictionary<string, DomainEntry> _domains = new(StringComparer.OrdinalIgnoreCase);
    // Provider entries enforce provider-wide concurrency and pacing. Circuits are narrower so one tenant MX
    // cannot suppress unrelated tenants hosted by the same provider.
    private readonly ConcurrentDictionary<string, ProviderEntry> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderCircuitEntry> _providerCircuits = new(StringComparer.OrdinalIgnoreCase);
    private long _domainCooldownEvents;
    private long _providerCooldownEvents;
    private long _pacingWaitMilliseconds;
    private long _halfOpenAttempts;
    private long _providerResumptions;
    private long _providerConcurrencyWaits;
    private long _providerPacingWaits;
    private long _providerRetries;
    private long _providerRetryExhaustions;

    public DomainSmtpProbeThrottle(IOptions<EmailValidationOptions> options)
        : this(options, TimeProvider.System, new DomainPacingJitter(), new DomainBackoffPolicy(options),
            new ProviderPolicyResolver(options), NullLogger<DomainSmtpProbeThrottle>.Instance)
    {
    }

    public DomainSmtpProbeThrottle(
        IOptions<EmailValidationOptions> options,
        TimeProvider timeProvider,
        IDomainPacingJitter jitter,
        IDomainBackoffPolicy backoff)
        : this(options, timeProvider, jitter, backoff, new ProviderPolicyResolver(options),
            NullLogger<DomainSmtpProbeThrottle>.Instance)
    {
    }

    public DomainSmtpProbeThrottle(
        IOptions<EmailValidationOptions> options,
        TimeProvider timeProvider,
        IDomainPacingJitter jitter,
        IDomainBackoffPolicy backoff,
        IProviderPolicyResolver policyResolver,
        ILogger<DomainSmtpProbeThrottle> logger)
    {
        _legacy = options.Value.Smtp;
        _options = options.Value.Scheduling;
        _timeProvider = timeProvider;
        _jitter = jitter;
        _backoff = backoff;
        _policyResolver = policyResolver;
        _logger = logger;
        _meter = new Meter("EmailValidation.ProviderPolicies");
        _policyBlockMetric = _meter.CreateCounter<long>("email_validation.provider.policy_blocks");
        _cooldownActivationMetric = _meter.CreateCounter<long>("email_validation.provider.cooldown_activations");
        _cooldownDurationMetric = _meter.CreateHistogram<double>(
            "email_validation.provider.cooldown_duration", "s");
        _halfOpenMetric = _meter.CreateCounter<long>("email_validation.provider.half_open_attempts");
        _resumptionMetric = _meter.CreateCounter<long>("email_validation.provider.resumptions");
        _concurrencyWaitMetric = _meter.CreateCounter<long>("email_validation.provider.concurrency_waits");
        _pacingWaitMetric = _meter.CreateCounter<long>("email_validation.provider.pacing_waits");
        _retryMetric = _meter.CreateCounter<long>("email_validation.provider.retries");
        _retryExhaustionMetric = _meter.CreateCounter<long>("email_validation.provider.retry_exhaustions");
        _globalGate = new SemaphoreSlim(EffectiveGlobalConcurrency());
    }

    public ProviderThrottleAvailability GetAvailability(SmtpThrottleContext context)
    {
        var policy = _policyResolver.Resolve(context.Provider);
        var key = CircuitKey(policy.ProviderKey, context);
        var circuitAvailability = _providerCircuits.TryGetValue(key, out var circuit)
            ? Availability(circuit)
            : ProviderThrottleAvailability.Available;
        var retryAfter = circuitAvailability.RetryAfter;
        var reason = circuitAvailability.Reason;
        var now = _timeProvider.GetUtcNow();
        if (_domains.TryGetValue(context.Domain, out var domain))
        {
            lock (domain.Sync)
            {
                var domainRetry = Max(domain.State.NextAllowedAttemptAt, domain.State.CooldownUntil);
                if (domainRetry > now && (retryAfter is null || domainRetry > retryAfter))
                {
                    retryAfter = domainRetry;
                    reason = "DomainCooldown";
                }
            }
        }
        if (_providers.TryGetValue(policy.ProviderKey, out var provider))
        {
            lock (provider.Sync)
            {
                var providerRetry = Max(provider.NextAllowedAttemptAt, provider.CooldownUntil);
                if (providerRetry > now && (retryAfter is null || providerRetry > retryAfter))
                {
                    retryAfter = providerRetry;
                    reason = "ProviderPacing";
                }
            }
        }
        return retryAfter > now
            ? new(false, retryAfter, reason ?? "LocalCooldown")
            : ProviderThrottleAvailability.Available;
    }

    public async ValueTask<ISmtpThrottleLease> AcquireAsync(
        SmtpThrottleContext context,
        CancellationToken cancellationToken = default)
    {
        var policy = _policyResolver.Resolve(context.Provider);
        var domain = _domains.GetOrAdd(context.Domain, key => new DomainEntry(
            key, Math.Max(1, policy.PerDomainConcurrency ?? EffectivePerDomainConcurrency())));
        var provider = _providers.GetOrAdd(policy.ProviderKey, key => new ProviderEntry(
            key, policy.PerProviderConcurrency));
        var circuit = _providerCircuits.GetOrAdd(
            CircuitKey(policy.ProviderKey, context),
            key => new ProviderCircuitEntry(key, policy.ProviderKey));
        var initialAvailability = Availability(circuit);
        if (!initialAvailability.CanProbe)
            return new UnavailableLease(initialAvailability.RetryAfter, initialAvailability.Reason);

        await domain.Gate.WaitAsync(cancellationToken);
        var providerAcquired = false;
        var globalAcquired = false;
        var domainPacingAcquired = false;
        var providerPacingAcquired = false;
        var providerActive = false;
        var halfOpen = false;
        try
        {
            lock (domain.Sync) domain.State.ActiveCount++;
            if (provider.Gate.CurrentCount == 0)
            {
                Interlocked.Increment(ref _providerConcurrencyWaits);
                _concurrencyWaitMetric.Add(1, ProviderTag(policy.ProviderKey));
            }
            await provider.Gate.WaitAsync(cancellationToken);
            providerAcquired = true;
            await domain.PacingGate.WaitAsync(cancellationToken);
            domainPacingAcquired = true;
            await provider.PacingGate.WaitAsync(cancellationToken);
            providerPacingAcquired = true;
            var readiness = await WaitUntilReadyAsync(domain, provider, circuit, policy, cancellationToken);
            if (!readiness.CanProbe)
            {
                provider.PacingGate.Release();
                providerPacingAcquired = false;
                domain.PacingGate.Release();
                domainPacingAcquired = false;
                provider.Gate.Release();
                providerAcquired = false;
                lock (domain.Sync) domain.State.ActiveCount--;
                domain.Gate.Release();
                return new UnavailableLease(readiness.RetryAfter, readiness.Reason);
            }
            halfOpen = readiness.HalfOpen;
            await _globalGate.WaitAsync(cancellationToken);
            globalAcquired = true;

            var now = _timeProvider.GetUtcNow();
            lock (domain.Sync)
            {
                domain.State.LastAttemptAt = now;
                var interval = EffectiveDomainInterval();
                domain.State.NextAllowedAttemptAt = now.Add(interval == 0
                    ? TimeSpan.Zero
                    : _jitter.Apply(TimeSpan.FromMilliseconds(interval),
                        _options.DomainIntervalJitterMilliseconds));
            }
            lock (provider.Sync)
            {
                provider.ActiveCount++;
                providerActive = true;
                provider.LastAttemptAt = now;
                provider.NextAllowedAttemptAt = now.AddMilliseconds(policy.DelayMilliseconds);
            }
            if (!halfOpen)
            {
                provider.PacingGate.Release();
                providerPacingAcquired = false;
            }
            domain.PacingGate.Release();
            domainPacingAcquired = false;
            return new Lease(this, domain, provider, circuit, halfOpen, policy.PolicyBlockCooldownMinutes);
        }
        catch
        {
            if (halfOpen) AbandonHalfOpen(circuit, policy.PolicyBlockCooldownMinutes);
            if (providerActive)
                lock (provider.Sync) provider.ActiveCount--;
            lock (domain.Sync) domain.State.ActiveCount--;
            if (globalAcquired) _globalGate.Release();
            if (providerPacingAcquired) provider.PacingGate.Release();
            if (domainPacingAcquired) domain.PacingGate.Release();
            if (providerAcquired) provider.Gate.Release();
            domain.Gate.Release();
            throw;
        }
    }

    public void RecordOutcome(SmtpThrottleContext context, SmtpProbeResult result)
    {
        var policy = _policyResolver.Resolve(context.Provider);
        var category = result.Evidence?.Category ?? SmtpResponseCategory.Unknown;
        if (!_domains.TryGetValue(context.Domain, out var domain)) return;
        var now = _timeProvider.GetUtcNow();
        lock (domain.Sync)
        {
            if (IsTemporary(category))
            {
                domain.State.ConsecutiveTemporaryFailures++;
                var decision = _backoff.Evaluate(
                    context.Provider, category, domain.State.ConsecutiveTemporaryFailures, now);
                domain.State.CooldownUntil = Max(domain.State.CooldownUntil, decision.NextAllowedAttemptAt);
                domain.State.NextAllowedAttemptAt = Max(domain.State.NextAllowedAttemptAt, decision.NextAllowedAttemptAt);
                Interlocked.Increment(ref _domainCooldownEvents);
            }
            else if (category is SmtpResponseCategory.Accepted or SmtpResponseCategory.RecipientRejected or
                     SmtpResponseCategory.MailboxFull)
            {
                domain.State.ConsecutiveTemporaryFailures = 0;
                domain.State.CooldownUntil = null;
            }
        }

        _providers.GetOrAdd(policy.ProviderKey, key => new ProviderEntry(
            key, policy.PerProviderConcurrency));
        var circuit = _providerCircuits.GetOrAdd(
            CircuitKey(policy.ProviderKey, context),
            key => new ProviderCircuitEntry(key, policy.ProviderKey));
        var opened = false;
        var resumed = false;
        lock (circuit.Sync)
        {
            if (IsPolicyBlock(result))
            {
                var cooldown = TimeSpan.FromMinutes(policy.PolicyBlockCooldownMinutes);
                circuit.CircuitState = ProviderCircuitState.Open;
                circuit.CooldownUntil = now.Add(cooldown);
                circuit.CooldownReason = result.Evidence?.Category.ToString() ?? result.Status.ToString();
                circuit.ConsecutiveTemporaryFailures++;
                Interlocked.Increment(ref _providerCooldownEvents);
                _policyBlockMetric.Add(1, ProviderTag(policy.ProviderKey));
                _cooldownActivationMetric.Add(1, ProviderTag(policy.ProviderKey));
                _cooldownDurationMetric.Record(cooldown.TotalSeconds, ProviderTag(policy.ProviderKey));
                opened = true;
            }
            else if (circuit.CircuitState == ProviderCircuitState.HalfOpen && IsConclusive(category))
            {
                circuit.CircuitState = ProviderCircuitState.Closed;
                circuit.ConsecutiveTemporaryFailures = 0;
                circuit.CooldownUntil = null;
                circuit.CooldownReason = null;
                Interlocked.Increment(ref _providerResumptions);
                _resumptionMetric.Add(1, ProviderTag(policy.ProviderKey));
                resumed = true;
            }
            else if (circuit.CircuitState == ProviderCircuitState.HalfOpen)
            {
                circuit.ConsecutiveTemporaryFailures++;
                var decision = _backoff.Evaluate(
                    context.Provider, SmtpResponseCategory.TemporaryFailure,
                    circuit.ConsecutiveTemporaryFailures, now);
                circuit.CircuitState = ProviderCircuitState.Open;
                circuit.CooldownUntil = decision.NextAllowedAttemptAt;
                circuit.CooldownReason = category.ToString();
                _cooldownActivationMetric.Add(1, ProviderTag(policy.ProviderKey));
                _cooldownDurationMetric.Record(decision.Cooldown.TotalSeconds, ProviderTag(policy.ProviderKey));
            }
            else if (IsConclusive(category))
            {
                circuit.ConsecutiveTemporaryFailures = 0;
                circuit.CooldownUntil = null;
            }
            else if (IsProviderPressure(category))
            {
                circuit.ConsecutiveTemporaryFailures++;
                var decision = _backoff.Evaluate(
                    context.Provider, category, circuit.ConsecutiveTemporaryFailures, now);
                circuit.CooldownUntil = Max(circuit.CooldownUntil, decision.NextAllowedAttemptAt);
                Interlocked.Increment(ref _providerCooldownEvents);
                _cooldownActivationMetric.Add(1, ProviderTag(policy.ProviderKey));
                _cooldownDurationMetric.Record(decision.Cooldown.TotalSeconds, ProviderTag(policy.ProviderKey));
            }
        }
        if (opened)
            _logger.LogWarning(
                "Provider policy block detected for {Provider}; verification paused for {CooldownMinutes} minutes",
                policy.ProviderKey, policy.PolicyBlockCooldownMinutes);
        else if (resumed)
            _logger.LogInformation("Provider resumed after a successful half-open validation: {Provider}", policy.ProviderKey);
    }

    public void RecordProviderRetry(MailProvider provider, bool exhausted)
    {
        var providerKey = _policyResolver.Resolve(provider).ProviderKey;
        if (exhausted)
        {
            Interlocked.Increment(ref _providerRetryExhaustions);
            _retryExhaustionMetric.Add(1, ProviderTag(providerKey));
        }
        else
        {
            Interlocked.Increment(ref _providerRetries);
            _retryMetric.Add(1, ProviderTag(providerKey));
        }
    }

    internal DomainPacingState? GetDomainState(string domain) =>
        _domains.TryGetValue(domain, out var entry) ? entry.State : null;

    internal ProviderRuntimeState? GetProviderState(MailProvider provider)
    {
        var key = _policyResolver.Resolve(provider).ProviderKey;
        if (!_providers.TryGetValue(key, out var entry)) return null;
        var circuits = _providerCircuits.Values
            .Where(circuit => string.Equals(circuit.Provider, key, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        DateTimeOffset? cooldownUntil = null;
        string? cooldownReason = null;
        var circuitState = ProviderCircuitState.Closed;
        foreach (var circuit in circuits)
        {
            lock (circuit.Sync)
            {
                if (circuit.CooldownUntil is not null &&
                    (cooldownUntil is null || circuit.CooldownUntil > cooldownUntil))
                {
                    cooldownUntil = circuit.CooldownUntil;
                    cooldownReason = circuit.CooldownReason;
                }
                if (circuit.CircuitState == ProviderCircuitState.Open)
                    circuitState = ProviderCircuitState.Open;
                else if (circuit.CircuitState == ProviderCircuitState.HalfOpen && circuitState == ProviderCircuitState.Closed)
                    circuitState = ProviderCircuitState.HalfOpen;
            }
        }
        lock (entry.Sync)
            return new(entry.Provider, entry.ActiveCount, entry.LastAttemptAt,
                Max(entry.NextAllowedAttemptAt, cooldownUntil), cooldownUntil, circuitState, cooldownReason);
    }

    public SmtpSchedulingSnapshot GetSnapshot()
    {
        var now = _timeProvider.GetUtcNow();
        return new(
            _domains.Count,
            _domains.Values.Count(entry => entry.State.ActiveCount > 0),
            _domains.Values.Count(entry => entry.State.CooldownUntil > now),
            _providers.Count,
            _providerCircuits.Values
                .Where(entry => entry.CooldownUntil > now)
                .Select(entry => entry.Provider)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            Interlocked.Read(ref _domainCooldownEvents),
            Interlocked.Read(ref _providerCooldownEvents),
            Interlocked.Read(ref _pacingWaitMilliseconds),
            Interlocked.Read(ref _halfOpenAttempts),
            Interlocked.Read(ref _providerResumptions),
            Interlocked.Read(ref _providerConcurrencyWaits),
            Interlocked.Read(ref _providerPacingWaits),
            Interlocked.Read(ref _providerRetries),
            Interlocked.Read(ref _providerRetryExhaustions));
    }

    private async Task<ProviderReadiness> WaitUntilReadyAsync(
        DomainEntry domain,
        ProviderEntry provider,
        ProviderCircuitEntry circuit,
        ProviderPolicy policy,
        CancellationToken cancellationToken)
    {
        var ownsHalfOpen = false;
        while (true)
        {
            DateTimeOffset domainNext;
            lock (domain.Sync)
                domainNext = Max(domain.State.NextAllowedAttemptAt, domain.State.CooldownUntil) ?? DateTimeOffset.MinValue;
            DateTimeOffset providerNext;
            DateTimeOffset circuitNext;
            var becameHalfOpen = false;
            lock (provider.Sync)
            {
                providerNext = provider.NextAllowedAttemptAt ?? DateTimeOffset.MinValue;
            }
            lock (circuit.Sync)
            {
                var now = _timeProvider.GetUtcNow();
                circuitNext = circuit.CooldownUntil ?? DateTimeOffset.MinValue;
                if (circuit.CircuitState == ProviderCircuitState.Open && circuitNext > now)
                    return new(false, false, circuitNext, circuit.CooldownReason ?? "ProviderPolicyCooldown");
                if (circuit.CircuitState == ProviderCircuitState.Open &&
                    circuitNext <= now && domainNext <= now && providerNext <= now)
                {
                    circuit.CircuitState = ProviderCircuitState.HalfOpen;
                    ownsHalfOpen = true;
                    becameHalfOpen = true;
                    Interlocked.Increment(ref _halfOpenAttempts);
                    _halfOpenMetric.Add(1, ProviderTag(policy.ProviderKey));
                }
            }
            if (becameHalfOpen)
                _logger.LogInformation(
                    "Provider circuit half-open for {Provider}; allowing one validation attempt", policy.ProviderKey);
            var next = Max(Max(domainNext, providerNext), circuitNext);
            var delay = next - _timeProvider.GetUtcNow();
            if (delay <= TimeSpan.Zero) return new(true, ownsHalfOpen);
            Interlocked.Add(ref _pacingWaitMilliseconds, (long)Math.Ceiling(delay.TotalMilliseconds));
            if (providerNext > _timeProvider.GetUtcNow())
            {
                Interlocked.Increment(ref _providerPacingWaits);
                _pacingWaitMetric.Add(1, ProviderTag(policy.ProviderKey));
            }
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
    }

    private int EffectiveGlobalConcurrency() =>
        Math.Max(1, _options.GlobalConcurrency > 0 ? _options.GlobalConcurrency : _legacy.GlobalConcurrency);
    private int EffectivePerDomainConcurrency() =>
        Math.Max(1, _options.PerDomainConcurrency > 0 ? _options.PerDomainConcurrency : _legacy.PerDomainConcurrency);
    private int EffectiveDomainInterval() =>
        Math.Max(0, _options.DomainMinIntervalMilliseconds >= 0
            ? _options.DomainMinIntervalMilliseconds
            : _legacy.DelayBetweenDomainRequestsMilliseconds);

    private static bool IsTemporary(SmtpResponseCategory category) => category is
        SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted or
        SmtpResponseCategory.RateLimited or SmtpResponseCategory.VerificationBlocked or
        SmtpResponseCategory.ConnectionRejected or SmtpResponseCategory.Timeout;
    private static bool IsProviderPressure(SmtpResponseCategory category) => category is
        SmtpResponseCategory.RateLimited or SmtpResponseCategory.Greylisted or
        SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.TemporaryFailure;
    private static bool IsConclusive(SmtpResponseCategory category) => category is
        SmtpResponseCategory.Accepted or SmtpResponseCategory.RecipientRejected or SmtpResponseCategory.MailboxFull;
    private static bool IsPolicyBlock(SmtpProbeResult result) =>
        (result.Evidence?.Category is SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.RateLimited) &&
        SmtpSenderFailureClassifier.Scope(result) is ValidationFailureScope.Provider or ValidationFailureScope.SourceIp;
    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    private static KeyValuePair<string, object?> ProviderTag(string provider) => new("provider", provider);

    private ProviderThrottleAvailability Availability(ProviderCircuitEntry circuit)
    {
        lock (circuit.Sync)
        {
            var now = _timeProvider.GetUtcNow();
            var retryAfter = circuit.CooldownUntil;
            return circuit.CircuitState == ProviderCircuitState.Open && retryAfter > now
                ? new(false, retryAfter, circuit.CooldownReason ?? "ProviderPolicyCooldown")
                : ProviderThrottleAvailability.Available;
        }
    }

    private static string CircuitKey(string providerKey, SmtpThrottleContext context)
    {
        // Preserve optional source/tenant dimensions for deployments that supply them; current callers at
        // minimum isolate provider policy blocks by normalized MX host.
        var mxHost = context.MxHost.Trim().TrimEnd('.').ToLowerInvariant();
        var outboundIp = string.IsNullOrWhiteSpace(context.OutboundIp) ? "default-ip" : context.OutboundIp.Trim();
        var tenant = string.IsNullOrWhiteSpace(context.Tenant) ? "default-tenant" : context.Tenant.Trim();
        return $"{providerKey}|{mxHost}|{outboundIp}|{tenant}";
    }

    private sealed class DomainEntry(string domain, int concurrency)
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Gate { get; } = new(concurrency);
        public SemaphoreSlim PacingGate { get; } = new(1, 1);
        public DomainPacingState State { get; } = new() { Domain = domain };
    }

    private sealed class ProviderEntry(string provider, int concurrency)
    {
        public string Provider { get; } = provider;
        public object Sync { get; } = new();
        public SemaphoreSlim Gate { get; } = new(concurrency);
        public SemaphoreSlim PacingGate { get; } = new(1, 1);
        public DateTimeOffset? NextAllowedAttemptAt { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public int ActiveCount { get; set; }
    }

    private sealed class ProviderCircuitEntry(string key, string provider)
    {
        public string Key { get; } = key;
        public string Provider { get; } = provider;
        public object Sync { get; } = new();
        public DateTimeOffset? CooldownUntil { get; set; }
        public int ConsecutiveTemporaryFailures { get; set; }
        public ProviderCircuitState CircuitState { get; set; }
        public string? CooldownReason { get; set; }
    }

    private sealed class Lease(
        DomainSmtpProbeThrottle owner,
        DomainEntry domain,
        ProviderEntry provider,
        ProviderCircuitEntry circuit,
        bool ownsHalfOpen,
        int policyBlockCooldownMinutes) : ISmtpThrottleLease
    {
        private int _disposed;
        public bool Acquired => true;
        public DateTimeOffset? RetryAfter => null;
        public string? Reason => null;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (ownsHalfOpen)
                {
                    owner.AbandonHalfOpen(circuit, policyBlockCooldownMinutes);
                    provider.PacingGate.Release();
                }
                lock (provider.Sync) provider.ActiveCount--;
                lock (domain.Sync) domain.State.ActiveCount--;
                owner._globalGate.Release();
                provider.Gate.Release();
                domain.Gate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnavailableLease(DateTimeOffset? retryAfter, string? reason) : ISmtpThrottleLease
    {
        public bool Acquired => false;
        public DateTimeOffset? RetryAfter { get; } = retryAfter;
        public string? Reason { get; } = reason;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record ProviderReadiness(
        bool CanProbe,
        bool HalfOpen,
        DateTimeOffset? RetryAfter = null,
        string? Reason = null);

    private void AbandonHalfOpen(ProviderCircuitEntry circuit, int cooldownMinutes)
    {
        lock (circuit.Sync)
        {
            if (circuit.CircuitState != ProviderCircuitState.HalfOpen) return;
            circuit.CircuitState = ProviderCircuitState.Open;
            circuit.CooldownUntil = _timeProvider.GetUtcNow().AddMinutes(cooldownMinutes);
            circuit.CooldownReason = "HalfOpenAttemptAbandoned";
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
        _globalGate.Dispose();
        foreach (var entry in _domains.Values)
        {
            entry.Gate.Dispose();
            entry.PacingGate.Dispose();
        }
        foreach (var entry in _providers.Values)
        {
            entry.Gate.Dispose();
            entry.PacingGate.Dispose();
        }
    }
}
