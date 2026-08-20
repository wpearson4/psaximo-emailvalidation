using System.Collections.Concurrent;
using EmailValidation.Core;
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
    private readonly SemaphoreSlim _globalGate;
    private readonly ConcurrentDictionary<string, DomainEntry> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<MailProvider, ProviderEntry> _providers = new();
    private long _domainCooldownEvents;
    private long _providerCooldownEvents;
    private long _pacingWaitMilliseconds;

    public DomainSmtpProbeThrottle(IOptions<EmailValidationOptions> options)
        : this(options, TimeProvider.System, new DomainPacingJitter(), new DomainBackoffPolicy(options)) { }

    public DomainSmtpProbeThrottle(
        IOptions<EmailValidationOptions> options,
        TimeProvider timeProvider,
        IDomainPacingJitter jitter,
        IDomainBackoffPolicy backoff)
    {
        _legacy = options.Value.Smtp;
        _options = options.Value.Scheduling;
        _timeProvider = timeProvider;
        _jitter = jitter;
        _backoff = backoff;
        _globalGate = new SemaphoreSlim(EffectiveGlobalConcurrency());
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        SmtpThrottleContext context,
        CancellationToken cancellationToken = default)
    {
        var policy = ProviderPolicy(context.Provider);
        var domain = _domains.GetOrAdd(context.Domain, key => new DomainEntry(
            key, Math.Max(1, policy.PerDomainConcurrency ?? EffectivePerDomainConcurrency())));
        var provider = _providers.GetOrAdd(context.Provider, key => new ProviderEntry(
            key, Math.Max(1, policy.PerProviderConcurrency ?? EffectivePerProviderConcurrency())));

        await domain.Gate.WaitAsync(cancellationToken);
        var providerAcquired = false;
        var globalAcquired = false;
        var domainPacingAcquired = false;
        var providerPacingAcquired = false;
        try
        {
            lock (domain.Sync) domain.State.ActiveCount++;
            await provider.Gate.WaitAsync(cancellationToken);
            providerAcquired = true;
            await domain.PacingGate.WaitAsync(cancellationToken);
            domainPacingAcquired = true;
            await provider.PacingGate.WaitAsync(cancellationToken);
            providerPacingAcquired = true;
            await WaitUntilReadyAsync(domain, provider, policy, cancellationToken);
            await _globalGate.WaitAsync(cancellationToken);
            globalAcquired = true;

            var now = _timeProvider.GetUtcNow();
            lock (domain.Sync)
            {
                domain.State.LastAttemptAt = now;
                var interval = EffectiveDomainInterval(policy);
                domain.State.NextAllowedAttemptAt = now.Add(interval == 0
                    ? TimeSpan.Zero
                    : _jitter.Apply(TimeSpan.FromMilliseconds(interval),
                        _options.DomainIntervalJitterMilliseconds));
            }
            lock (provider.Sync)
                provider.NextAllowedAttemptAt = now.AddMilliseconds(EffectiveProviderInterval(policy));
            provider.PacingGate.Release();
            providerPacingAcquired = false;
            domain.PacingGate.Release();
            domainPacingAcquired = false;
            return new Lease(this, domain, provider);
        }
        catch
        {
            if (globalAcquired) _globalGate.Release();
            if (providerPacingAcquired) provider.PacingGate.Release();
            if (domainPacingAcquired) domain.PacingGate.Release();
            if (providerAcquired) provider.Gate.Release();
            lock (domain.Sync) domain.State.ActiveCount--;
            domain.Gate.Release();
            throw;
        }
    }

    public void RecordOutcome(SmtpThrottleContext context, SmtpProbeResult result)
    {
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

        if (context.Provider == MailProvider.Unknown) return;
        var provider = _providers.GetOrAdd(context.Provider, key => new ProviderEntry(
            key, EffectivePerProviderConcurrency()));
        lock (provider.Sync)
        {
            if (category is SmtpResponseCategory.Accepted or SmtpResponseCategory.RecipientRejected or
                SmtpResponseCategory.MailboxFull)
            {
                provider.ConsecutiveTemporaryFailures = 0;
                provider.CooldownUntil = null;
                return;
            }
            if (!IsProviderPressure(category)) return;
            provider.ConsecutiveTemporaryFailures++;
            var decision = _backoff.Evaluate(
                context.Provider, category, provider.ConsecutiveTemporaryFailures, now);
            provider.CooldownUntil = Max(provider.CooldownUntil, decision.NextAllowedAttemptAt);
            provider.NextAllowedAttemptAt = Max(provider.NextAllowedAttemptAt, decision.NextAllowedAttemptAt);
            Interlocked.Increment(ref _providerCooldownEvents);
        }
    }

    internal DomainPacingState? GetDomainState(string domain) =>
        _domains.TryGetValue(domain, out var entry) ? entry.State : null;

    public SmtpSchedulingSnapshot GetSnapshot()
    {
        var now = _timeProvider.GetUtcNow();
        return new(
            _domains.Count,
            _domains.Values.Count(entry => entry.State.ActiveCount > 0),
            _domains.Values.Count(entry => entry.State.CooldownUntil > now),
            _providers.Count,
            _providers.Values.Count(entry => entry.CooldownUntil > now),
            Interlocked.Read(ref _domainCooldownEvents),
            Interlocked.Read(ref _providerCooldownEvents),
            Interlocked.Read(ref _pacingWaitMilliseconds));
    }

    private async Task WaitUntilReadyAsync(
        DomainEntry domain,
        ProviderEntry provider,
        ProviderSchedulingOptions policy,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            DateTimeOffset next;
            lock (domain.Sync)
                next = Max(domain.State.NextAllowedAttemptAt, domain.State.CooldownUntil) ?? DateTimeOffset.MinValue;
            lock (provider.Sync)
                next = Max(next, Max(provider.NextAllowedAttemptAt, provider.CooldownUntil) ?? DateTimeOffset.MinValue);
            var delay = next - _timeProvider.GetUtcNow();
            if (delay <= TimeSpan.Zero) return;
            Interlocked.Add(ref _pacingWaitMilliseconds, (long)Math.Ceiling(delay.TotalMilliseconds));
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
    }

    private int EffectiveGlobalConcurrency() =>
        Math.Max(1, _options.GlobalConcurrency > 0 ? _options.GlobalConcurrency : _legacy.GlobalConcurrency);
    private int EffectivePerDomainConcurrency() =>
        Math.Max(1, _options.PerDomainConcurrency > 0 ? _options.PerDomainConcurrency : _legacy.PerDomainConcurrency);
    private int EffectivePerProviderConcurrency() =>
        Math.Max(1, _options.PerProviderConcurrency > 0 ? _options.PerProviderConcurrency : _legacy.PerProviderConcurrency);
    private int EffectiveDomainInterval(ProviderSchedulingOptions policy) =>
        Math.Max(0, policy.MinIntervalMilliseconds ??
            (_options.DomainMinIntervalMilliseconds >= 0
                ? _options.DomainMinIntervalMilliseconds
                : _legacy.DelayBetweenDomainRequestsMilliseconds));
    private int EffectiveProviderInterval(ProviderSchedulingOptions policy) =>
        Math.Max(0, policy.MinIntervalMilliseconds ?? _options.ProviderMinIntervalMilliseconds);
    private ProviderSchedulingOptions ProviderPolicy(MailProvider provider) =>
        _options.ProviderPolicies.TryGetValue(provider, out var policy) ? policy : new();

    private static bool IsTemporary(SmtpResponseCategory category) => category is
        SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted or
        SmtpResponseCategory.RateLimited or SmtpResponseCategory.VerificationBlocked or
        SmtpResponseCategory.ConnectionRejected or SmtpResponseCategory.Timeout;
    private static bool IsProviderPressure(SmtpResponseCategory category) => category is
        SmtpResponseCategory.RateLimited or SmtpResponseCategory.Greylisted or
        SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.TemporaryFailure;
    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private sealed class DomainEntry(string domain, int concurrency)
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Gate { get; } = new(concurrency);
        public SemaphoreSlim PacingGate { get; } = new(1, 1);
        public DomainPacingState State { get; } = new() { Domain = domain };
    }

    private sealed class ProviderEntry(MailProvider provider, int concurrency)
    {
        public MailProvider Provider { get; } = provider;
        public object Sync { get; } = new();
        public SemaphoreSlim Gate { get; } = new(concurrency);
        public SemaphoreSlim PacingGate { get; } = new(1, 1);
        public DateTimeOffset? NextAllowedAttemptAt { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }
        public int ConsecutiveTemporaryFailures { get; set; }
    }

    private sealed class Lease(
        DomainSmtpProbeThrottle owner,
        DomainEntry domain,
        ProviderEntry provider) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner._globalGate.Release();
                provider.Gate.Release();
                lock (domain.Sync) domain.State.ActiveCount--;
                domain.Gate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
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
