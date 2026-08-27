using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using EmailValidation.Core;

namespace EmailValidation.Application;

public sealed class DomainIntelligenceFreshnessPolicy(
    IOptions<EmailValidationOptions> options) : IDomainIntelligenceFreshnessPolicy
{
    private readonly EmailValidationOptions _options = options.Value;

    public DomainIntelligenceReuseDecision Evaluate(
        DomainIntelligence existing,
        DomainIntelligence? current,
        DateTimeOffset now)
    {
        if (!_options.DomainIntelligence.Enabled)
            return new(false, false, "Domain intelligence is disabled.");
        if (existing.EvidenceExpiresAt is not { } expiresAt || expiresAt <= now)
            return new(false, false, "Domain intelligence is stale.");
        if (!string.Equals(existing.IntelligencePolicyVersion, _options.DomainIntelligence.PolicyVersion,
                StringComparison.Ordinal))
            return new(false, false, "The domain-intelligence policy version changed.");
        if (current is null)
            return new(true, IsCatchAllFresh(existing, now), "Fresh intelligence can be reused.");

        var topologyMatches = string.Equals(
            Fingerprints.Mx(existing), Fingerprints.Mx(current), StringComparison.Ordinal);
        var providerMatches = string.Equals(
            Fingerprints.Provider(existing), Fingerprints.Provider(current), StringComparison.Ordinal);
        var authenticationMatches = string.Equals(
            existing.AuthenticationFingerprint, current.AuthenticationFingerprint, StringComparison.Ordinal);
        return topologyMatches && providerMatches && authenticationMatches
            ? new(true, IsCatchAllFresh(existing, now), "Routing, provider, and authentication intelligence are compatible.")
            : new(false, topologyMatches && providerMatches && IsCatchAllFresh(existing, now),
                "Routing, provider, or authentication intelligence changed.");
    }

    private bool IsCatchAllFresh(DomainIntelligence intelligence, DateTimeOffset now)
    {
        var observedAt = intelligence.CatchAll.ObservedAt ?? intelligence.ObservedAt;
        return observedAt != default &&
            observedAt.AddMinutes(Math.Max(0, _options.CatchAll.CacheMinutes)) > now;
    }
}

public sealed class DomainIntelligenceService : IDomainIntelligenceService, IDisposable
{
    private static readonly Meter Meter = new("EmailValidation.DomainIntelligence", "1.0.0");
    private static readonly Counter<long> MemoryHits = Meter.CreateCounter<long>("domain_intelligence_memory_hit");
    private static readonly Counter<long> PersistentHits = Meter.CreateCounter<long>("domain_intelligence_persistent_hit");
    private static readonly Counter<long> LiveLookups = Meter.CreateCounter<long>("domain_intelligence_live_lookup");
    private static readonly Counter<long> Refreshes = Meter.CreateCounter<long>("domain_intelligence_refresh");
    private static readonly Counter<long> TopologyChanges = Meter.CreateCounter<long>("domain_topology_change");
    private static readonly Counter<long> CatchAllReused = Meter.CreateCounter<long>("catch_all_reused");
    private static readonly Counter<long> CatchAllLive = Meter.CreateCounter<long>("catch_all_live_probe");
    private static readonly Counter<long> SingleFlightJoins = Meter.CreateCounter<long>("domain_single_flight_join");

    private readonly IMailRoutingAnalyzer _mailRouting;
    private readonly IDnsSecurityAnalyzer _dnsSecurity;
    private readonly IEmailAuthenticationAnalyzer _authentication;
    private readonly IDisposableEmailDomainProvider _disposable;
    private readonly IDomainIntelligenceEvaluator _supplemental;
    private readonly IMailProviderDetector _providerDetector;
    private readonly ICatchAllDetector _catchAllDetector;
    private readonly IDomainValidationCache _cache;
    private readonly IValidationPlanBuilder _planBuilder;
    private readonly IDomainIntelligenceFreshnessPolicy _freshness;
    private readonly IValidationPersistenceMetrics _persistenceMetrics;
    private readonly EmailValidationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DomainIntelligenceService> _logger;
    private readonly DomainSingleFlight<DomainBaseAcquisition> _baseFlights = new();
    private readonly DomainSingleFlight<CatchAllAcquisition> _catchAllFlights = new();
    private readonly SemaphoreSlim _analysisLimit;

    public DomainIntelligenceService(
        IMailRoutingAnalyzer mailRouting,
        IDnsSecurityAnalyzer dnsSecurity,
        IEmailAuthenticationAnalyzer authentication,
        IDisposableEmailDomainProvider disposable,
        IDomainIntelligenceEvaluator supplemental,
        IMailProviderDetector providerDetector,
        ICatchAllDetector catchAllDetector,
        IDomainValidationCache cache,
        IValidationPlanBuilder planBuilder,
        IDomainIntelligenceFreshnessPolicy freshness,
        IValidationPersistenceMetrics persistenceMetrics,
        IOptions<EmailValidationOptions> options,
        TimeProvider timeProvider,
        ILogger<DomainIntelligenceService> logger)
    {
        _mailRouting = mailRouting;
        _dnsSecurity = dnsSecurity;
        _authentication = authentication;
        _disposable = disposable;
        _supplemental = supplemental;
        _providerDetector = providerDetector;
        _catchAllDetector = catchAllDetector;
        _cache = cache;
        _planBuilder = planBuilder;
        _freshness = freshness;
        _persistenceMetrics = persistenceMetrics;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _analysisLimit = new SemaphoreSlim(
            Math.Max(1, _options.DomainIntelligence.MaximumConcurrentAnalyses),
            Math.Max(1, _options.DomainIntelligence.MaximumConcurrentAnalyses));
    }

    public async Task<DomainIntelligence> GetAsync(
        string domain,
        CancellationToken cancellationToken = default) =>
        (await AcquireAsync(domain, false, cancellationToken).ConfigureAwait(false)).Intelligence;

    public async Task<DomainIntelligenceAcquisition> AcquireAsync(
        string domain,
        bool allowCatchAllProbe,
        CancellationToken cancellationToken = default)
    {
        domain = Normalize(domain);
        var now = _timeProvider.GetUtcNow();
        DomainBaseAcquisition baseResult;
        if (_cache.TryGet(domain, out var hot) && hot is not null && _freshness.Evaluate(hot, null, now).CanReuse)
        {
            MemoryHits.Add(1);
            baseResult = new(hot, DomainIntelligenceSource.MemoryCache, 0, false);
        }
        else
        {
            var flight = await _baseFlights.ExecuteAsync(
                domain,
                token => LoadOrAnalyzeAsync(domain, token),
                cancellationToken).ConfigureAwait(false);
            if (flight.Joined)
            {
                SingleFlightJoins.Add(1);
                baseResult = flight.Value with { Source = DomainIntelligenceSource.JoinedInFlight };
            }
            else
            {
                baseResult = flight.Value;
            }
        }

        var data = baseResult.Intelligence;
        var catchAllProbes = 0;
        var plan = _planBuilder.Build(
            data,
            allowCatchAllProbe,
            baseResult.Source is not DomainIntelligenceSource.LiveAnalysis,
            _options.Policy.ToVersions(),
            _timeProvider.GetUtcNow());
        if (plan.PerformCatchAllProbe)
        {
            var catchAllFlight = await _catchAllFlights.ExecuteAsync(
                domain,
                token => RefreshCatchAllAsync(domain, token),
                cancellationToken).ConfigureAwait(false);
            if (catchAllFlight.Joined) SingleFlightJoins.Add(1);
            data = catchAllFlight.Value.Intelligence;
            catchAllProbes = catchAllFlight.Joined ? 0 : catchAllFlight.Value.Probes;
        }
        else if (plan.UsePersistedCatchAll)
        {
            CatchAllReused.Add(1);
        }

        plan = _planBuilder.Build(
            data,
            allowCatchAllProbe,
            baseResult.Source is not DomainIntelligenceSource.LiveAnalysis || catchAllProbes == 0,
            _options.Policy.ToVersions(),
            _timeProvider.GetUtcNow());
        return new(data, baseResult.Source, catchAllProbes, baseResult.AnalysisDurationMs, plan);
    }

    private async Task<DomainBaseAcquisition> LoadOrAnalyzeAsync(
        string domain,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var existing = await _cache.GetAsync(domain, cancellationToken).ConfigureAwait(false);
        if (existing is not null && _freshness.Evaluate(existing, null, now).CanReuse)
        {
            PersistentHits.Add(1);
            return new(existing, DomainIntelligenceSource.PersistentStore, 0, false);
        }

        await _analysisLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LiveLookups.Add(1);
            if (existing is not null) Refreshes.Add(1);
            var watch = Stopwatch.StartNew();
            var routingTask = _mailRouting.AnalyzeAsync(domain, cancellationToken);
            var securityTask = IsolateAsync(
                () => _dnsSecurity.AnalyzeAsync(domain, cancellationToken),
                DnsSecurityIntelligence.Unknown,
                "DNSSEC",
                domain,
                cancellationToken);
            var authenticationTask = IsolateAsync(
                () => _authentication.AnalyzeAsync(domain, cancellationToken),
                EmailAuthenticationIntelligence.Unknown,
                "email authentication",
                domain,
                cancellationToken);
            var disposableTask = IsolateAsync(
                async () => await _disposable.GetAsync(domain, cancellationToken).ConfigureAwait(false),
                DisposableDomainResult.Unknown,
                "disposable-domain",
                domain,
                cancellationToken);
            await Task.WhenAll(routingTask, securityTask, authenticationTask, disposableTask).ConfigureAwait(false);
            var routing = await routingTask.ConfigureAwait(false);
            var dns = new DnsLookupResult(
                routing.Status,
                routing.DomainExists,
                routing.Routes,
                routing.UsedAddressFallback,
                routing.LookupDuration,
                routing.Error,
                routing.ExplicitNullMx,
                routing.TimeToLive,
                routing.Ipv4Addresses,
                routing.Ipv6Addresses);
            var supplemental = await IsolateAsync(
                () => _supplemental.EvaluateAsync(domain, dns, cancellationToken),
                new SupplementalDomainIntelligence(
                    DisposableDomainResult.Unknown,
                    false,
                    ToxicDomainResult.Unknown,
                    MxForwardResult.Unknown,
                    DomainAgeResult.Unknown,
                    MailInfrastructureResult.Unknown,
                    0),
                "supplemental domain",
                domain,
                cancellationToken).ConfigureAwait(false);
            var detectedProvider = _providerDetector.DetectWithConfidence(domain, routing.Routes);
            var provider = detectedProvider with
            {
                Evidence = detectedProvider.Evidence is { Count: > 0 }
                    ? detectedProvider.Evidence
                    : ["MxTopology"],
                DetectedAtUtc = now,
                DetectionVersion = _options.Policy.ProviderStrategyVersion
            };
            var authentication = await authenticationTask.ConfigureAwait(false);
            var disposable = await disposableTask.ConfigureAwait(false);
            var mxFingerprint = provider.TopologyFingerprint;
            var providerFingerprint = Fingerprints.CreateProvider(provider);
            var authenticationFingerprint = Fingerprints.CreateAuthentication(authentication);
            var topologyChanged = existing is not null &&
                (!string.Equals(Fingerprints.Mx(existing), mxFingerprint, StringComparison.Ordinal) ||
                 !ProviderCompatible(existing, provider, providerFingerprint));
            var authenticationChanged = existing is not null &&
                !string.Equals(existing.AuthenticationFingerprint, authenticationFingerprint, StringComparison.Ordinal);
            var changed = topologyChanged || authenticationChanged;
            if (topologyChanged) TopologyChanges.Add(1);
            var catchAllTopologyCompatible = existing is not null &&
                string.Equals(Fingerprints.Mx(existing), mxFingerprint, StringComparison.Ordinal) &&
                ProviderCompatible(existing, provider, providerFingerprint) &&
                string.Equals(existing.StrategyVersion, _options.Policy.ProviderStrategyVersion, StringComparison.Ordinal);
            var lifetime = DomainLifetime(routing.TimeToLive);
            var intelligence = new DomainIntelligence
            {
                Domain = domain,
                DomainExists = routing.DomainExists,
                Dns = dns,
                MailRouting = routing,
                Provider = provider,
                DnsSecurity = await securityTask.ConfigureAwait(false),
                Authentication = authentication,
                Disposable = disposable.Status is DisposableDomainStatus.KnownDisposable or DisposableDomainStatus.LikelyDisposable,
                DisposableIntelligence = disposable,
                FreeEmailProvider = supplemental.FreeEmailProvider,
                ToxicDomain = supplemental.ToxicDomain,
                MxForward = supplemental.MxForward,
                DomainAge = supplemental.DomainAge,
                MailInfrastructure = supplemental.MailInfrastructure,
                // Compatible stale catch-all evidence remains refresh context, but the
                // planner still observes its old timestamp and requires a live refresh.
                CatchAll = catchAllTopologyCompatible
                    ? existing!.CatchAll
                    : new CatchAllDetectionResult(CatchAllStatus.NotAttempted, 0, 0, 0, 0),
                Behavior = catchAllTopologyCompatible ? existing!.Behavior : null,
                ObservedAt = now,
                EvidenceExpiresAt = now.Add(lifetime),
                StrategyVersion = _options.Policy.ProviderStrategyVersion,
                MxTopologyFingerprint = mxFingerprint,
                ProviderFingerprint = providerFingerprint,
                AuthenticationFingerprint = authenticationFingerprint,
                CatchAllFingerprint = catchAllTopologyCompatible
                    ? existing!.CatchAllFingerprint ?? Fingerprints.CreateCatchAll(existing.CatchAll)
                    : null,
                FirstObservedUtc = existing?.FirstObservedUtc is { } first && first != default
                    ? first
                    : existing?.ObservedAt is { } observed && observed != default ? observed : now,
                LastObservedUtc = now,
                LastChangedUtc = changed ? now : existing?.LastChangedUtc,
                ChangeCount = (existing?.ChangeCount ?? 0) + (changed ? 1 : 0),
                IntelligencePolicyVersion = _options.DomainIntelligence.PolicyVersion
            };
            await _cache.StoreAsync(intelligence, lifetime, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            return new(intelligence, DomainIntelligenceSource.LiveAnalysis, watch.ElapsedMilliseconds, changed);
        }
        finally
        {
            _analysisLimit.Release();
        }
    }

    private async Task<CatchAllAcquisition> RefreshCatchAllAsync(
        string domain,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var current = await _cache.GetAsync(domain, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Domain intelligence was not available for catch-all analysis.");
        var plan = _planBuilder.Build(
            current,
            true,
            true,
            _options.Policy.ToVersions(),
            now);
        if (!plan.PerformCatchAllProbe)
        {
            CatchAllReused.Add(1);
            return new(current, 0);
        }
        var selectedMx = current.MxRecords
            .OrderBy(record => record.Preference)
            .ThenBy(record => record.Host, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.Host;
        if (selectedMx is null) return new(current, 0);

        CatchAllLive.Add(1);
        var detection = await _catchAllDetector.DetectAsync(
            domain, selectedMx, current.Provider.Provider, cancellationToken).ConfigureAwait(false);
        var probeCount = detection.Probes;
        detection = detection with
        {
            ObservedAt = detection.ObservedAt ?? now,
            StrategyVersion = string.IsNullOrWhiteSpace(detection.StrategyVersion)
                ? _options.Policy.ProviderStrategyVersion
                : detection.StrategyVersion,
            RefreshAttemptedAt = detection.RefreshAttemptedAt ?? now
        };
        if (detection.Status == CatchAllStatus.Unknown && CanPreserveAfterInconclusiveRefresh(current))
        {
            detection = current.CatchAll with
            {
                Detail = $"{current.CatchAll.Detail} The latest refresh was inconclusive; historical evidence was preserved.",
                RefreshAttemptedAt = now,
                RefreshInconclusive = true
            };
        }
        var updated = current with
        {
            CatchAll = detection,
            CatchAllFingerprint = Fingerprints.CreateCatchAll(detection),
            LastObservedUtc = now,
            ObservedAt = now
        };
        await _cache.StoreAsync(updated, DomainLifetime(current.MailRouting?.TimeToLive), cancellationToken).ConfigureAwait(false);
        if (detection.Status == CatchAllStatus.LikelyCatchAll &&
            current.CatchAll.Status != CatchAllStatus.LikelyCatchAll)
            _persistenceMetrics.RecordCatchAllDiscovered();
        return new(updated, probeCount);
    }

    private async Task<T> IsolateAsync<T>(
        Func<Task<T>> action,
        T fallback,
        string capability,
        string domain,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning("{Capability} intelligence failed for {Domain}; validation will continue ({ErrorType})",
                capability, domain, exception.GetType().Name);
            return fallback;
        }
    }

    private bool CanPreserveAfterInconclusiveRefresh(DomainIntelligence current) =>
        current.CatchAll.Status == CatchAllStatus.LikelyCatchAll &&
        string.Equals(current.StrategyVersion, _options.Policy.ProviderStrategyVersion, StringComparison.Ordinal) &&
        string.Equals(Fingerprints.Mx(current), current.Provider.TopologyFingerprint, StringComparison.Ordinal);

    private static bool ProviderCompatible(
        DomainIntelligence existing,
        ProviderDetectionResult current,
        string currentFingerprint)
    {
        if (!string.IsNullOrWhiteSpace(existing.ProviderFingerprint))
            return string.Equals(existing.ProviderFingerprint, currentFingerprint, StringComparison.Ordinal);

        // Records written before provider fingerprints were introduced do not
        // contain the richer family/gateway fields. Treat unknown legacy fields
        // as wildcards while still requiring the detected provider to agree.
        return existing.Provider.Provider == current.Provider &&
            (existing.Provider.Family == ProviderFamily.Unknown || existing.Provider.Family == current.Family) &&
            (existing.Provider.GatewayProvider == GatewayProvider.Unknown ||
             existing.Provider.GatewayProvider == current.GatewayProvider);
    }

    private TimeSpan DomainLifetime(TimeSpan? routingTtl)
    {
        var configured = TimeSpan.FromHours(Math.Max(0, Math.Min(
            _options.DomainIntelligence.PersistentFreshnessHours,
            _options.DomainIntelligence.MaximumFreshnessHours)));
        var legacy = TimeSpan.FromMinutes(Math.Max(0, _options.Dns.CacheMinutes));
        var policyLifetime = configured == TimeSpan.Zero ? legacy : configured;
        var lower = TimeSpan.FromMinutes(Math.Max(0, _options.DomainIntelligence.MinimumFreshnessMinutes));
        var ttlLifetime = routingTtl is { } ttl && ttl > TimeSpan.Zero ? ttl : policyLifetime;
        return ttlLifetime < lower ? lower : ttlLifetime > policyLifetime ? policyLifetime : ttlLifetime;
    }

    private static string Normalize(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();

    public void Dispose()
    {
        _analysisLimit.Dispose();
        _baseFlights.Dispose();
        _catchAllFlights.Dispose();
    }

    private sealed record DomainBaseAcquisition(
        DomainIntelligence Intelligence,
        DomainIntelligenceSource Source,
        long AnalysisDurationMs,
        bool Changed);

    private sealed record CatchAllAcquisition(DomainIntelligence Intelligence, int Probes);
}

internal static class Fingerprints
{
    public static string? Mx(DomainIntelligence intelligence) =>
        intelligence.MxTopologyFingerprint ?? intelligence.Provider.TopologyFingerprint;

    public static string? Provider(DomainIntelligence intelligence) =>
        intelligence.ProviderFingerprint ?? CreateProvider(intelligence.Provider);

    public static string CreateProvider(ProviderDetectionResult provider) => Hash(
        $"{provider.Provider}|{provider.Family}|{provider.GatewayProvider}|{provider.MxHost?.ToLowerInvariant()}");

    public static string CreateAuthentication(EmailAuthenticationIntelligence authentication) => Hash(
        $"{authentication.Spf.State}|{authentication.Spf.Record}|{authentication.Dmarc.State}|" +
        $"{authentication.Dmarc.Policy}|{authentication.Dmarc.SubdomainPolicy}|{authentication.Dmarc.Percentage}|" +
        $"{authentication.Dkim.State}");

    public static string CreateCatchAll(CatchAllDetectionResult catchAll) => Hash(
        $"{catchAll.Status}|{catchAll.ReasonCode}|{catchAll.Confidence:F6}|{catchAll.StrategyVersion}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed class DomainSingleFlight<T> : IDisposable
{
    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.OrdinalIgnoreCase);

    public async Task<(T Value, bool Joined)> ExecuteAsync(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        var candidate = new Flight(factory);
        var operation = _flights.GetOrAdd(key, candidate);
        var joined = !ReferenceEquals(candidate, operation);
        if (joined) candidate.Dispose();
        Interlocked.Increment(ref operation.Waiters);
        var task = operation.Task.Value;
        _ = task.ContinueWith(
            _ =>
            {
                _flights.TryRemove(new KeyValuePair<string, Flight>(key, operation));
                operation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            return (await task.WaitAsync(cancellationToken).ConfigureAwait(false), joined);
        }
        finally
        {
            if (Interlocked.Decrement(ref operation.Waiters) == 0 && !task.IsCompleted)
            {
                _flights.TryRemove(new KeyValuePair<string, Flight>(key, operation));
                operation.Cancellation.Cancel();
            }
            if (task.IsCompleted) _flights.TryRemove(new KeyValuePair<string, Flight>(key, operation));
        }
    }

    public void Dispose()
    {
        foreach (var flight in _flights.Values) flight.Dispose();
        _flights.Clear();
    }

    private sealed class Flight : IDisposable
    {
        public Flight(Func<CancellationToken, Task<T>> factory)
        {
            Task = new Lazy<Task<T>>(
                () => factory(Cancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public CancellationTokenSource Cancellation { get; } = new();
        public Lazy<Task<T>> Task { get; }
        public int Waiters;
        public void Dispose() => Cancellation.Dispose();
    }
}
