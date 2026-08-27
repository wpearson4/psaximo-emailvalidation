using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class LocalOutboundIdentityDiscovery : ILocalOutboundIdentityDiscovery
{
    public Task<IReadOnlySet<string>> GetBoundIpv4AddressesAsync(
        string interfaceName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => string.Equals(item.Name, interfaceName, StringComparison.Ordinal))
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(item => item.Address.ToString())
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlySet<string>>(addresses);
    }

    public Task<LocalOutboundIdentityBinding> InspectAsync(
        string interfaceName,
        IPAddress address,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var expected = interfaces.FirstOrDefault(item =>
            string.Equals(item.Name, interfaceName, StringComparison.Ordinal));
        var actual = interfaces.FirstOrDefault(item => item.GetIPProperties().UnicastAddresses
            .Any(candidate => candidate.Address.Equals(address)));
        return Task.FromResult(new LocalOutboundIdentityBinding(
            interfaceName,
            expected is not null,
            expected?.OperationalStatus == OperationalStatus.Up,
            actual is not null && string.Equals(actual.Name, interfaceName, StringComparison.Ordinal),
            actual?.Name));
    }
}

internal sealed class OutboundIdentityDnsResolver(IDnsWireQueryClient dns) : IOutboundIdentityDnsResolver
{
    public Task<OutboundIdentityDnsQueryResult> ResolvePtrAsync(
        IPAddress address,
        CancellationToken cancellationToken = default)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return Task.FromResult(new OutboundIdentityDnsQueryResult(
                OutboundIdentityDnsQueryStatus.NoData, [], []));
        var reverse = string.Join('.', address.GetAddressBytes().Reverse()) + ".in-addr.arpa";
        return ResolveAsync(reverse, DnsRecordType.Ptr, cancellationToken);
    }

    public Task<OutboundIdentityDnsQueryResult> ResolveIpv4Async(
        string hostName,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(hostName, DnsRecordType.A, cancellationToken);

    private async Task<OutboundIdentityDnsQueryResult> ResolveAsync(
        string name,
        DnsRecordType type,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await dns.QueryAsync(name, type, false, false, cancellationToken).ConfigureAwait(false);
            var status = response.ResponseCode switch
            {
                0 when type == DnsRecordType.Ptr && (response.HostNames?.Count ?? 0) == 0 =>
                    OutboundIdentityDnsQueryStatus.NoData,
                0 when type == DnsRecordType.A && (response.Addresses?.Count ?? 0) == 0 =>
                    OutboundIdentityDnsQueryStatus.NoData,
                0 => OutboundIdentityDnsQueryStatus.Success,
                3 => OutboundIdentityDnsQueryStatus.NotFound,
                2 => OutboundIdentityDnsQueryStatus.TemporaryFailure,
                _ => OutboundIdentityDnsQueryStatus.ResolverUnavailable
            };
            return new(status,
                response.HostNames ?? [],
                response.Addresses ?? [],
                response.MinimumTtlSeconds is { } ttl ? TimeSpan.FromSeconds(ttl) : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or IOException or TimeoutException or OperationCanceledException)
        {
            return new(OutboundIdentityDnsQueryStatus.TemporaryFailure, [], []);
        }
        catch (FormatException)
        {
            return new(OutboundIdentityDnsQueryStatus.MalformedResponse, [], []);
        }
    }
}

public sealed class ForwardConfirmedReverseDnsValidator(
    IOptions<EmailValidationOptions> options,
    IOutboundIdentityDnsResolver dns,
    ILocalOutboundIdentityDiscovery discovery,
    OutboundIdentityReadinessPolicy policy,
    TimeProvider timeProvider,
    ILogger<ForwardConfirmedReverseDnsValidator> logger) : IForwardConfirmedReverseDnsValidator
{
    private static readonly Meter Meter = new("EmailValidation.OutboundIdentityDns", "1.0.0");
    private static readonly Counter<long> Validations = Meter.CreateCounter<long>("outbound_identity_dns_validation_total");
    private static readonly Counter<long> Valid = Meter.CreateCounter<long>("outbound_identity_dns_valid_total");
    private static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("outbound_identity_dns_cache_hit_total");
    private static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("outbound_identity_dns_cache_miss_total");
    private static readonly Counter<long> SingleFlightJoins = Meter.CreateCounter<long>("outbound_identity_dns_single_flight_join_total");
    private static readonly Counter<long> LastKnownGoodUsed = Meter.CreateCounter<long>("outbound_identity_last_known_good_used_total");
    private readonly OutboundIdentityOptions _options = options.Value.OutboundIdentities;
    private readonly ConcurrentDictionary<string, DnsCacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<DnsCacheEntry>>> _flights = new(StringComparer.Ordinal);

    public async Task<ForwardConfirmedReverseDnsState> ValidateAsync(
        OutboundIdentity identity,
        CancellationToken cancellationToken = default) =>
        (await GetReadinessAsync(identity, false, cancellationToken).ConfigureAwait(false)).DnsState;

    public async Task<OutboundIdentityDnsReadiness> GetReadinessAsync(
        OutboundIdentity identity,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var local = await discovery.InspectAsync(
            identity.InterfaceName, identity.Address, cancellationToken).ConfigureAwait(false);
        var settings = _options.DnsReadiness;
        if (!_options.RequireForwardConfirmedReverseDns || !settings.Enabled ||
            settings.Mode == OutboundIdentityDnsReadinessMode.Disabled)
            return Disabled(identity, local, timeProvider.GetUtcNow(), settings.ValidationPolicyVersion);

        var key = CacheKey(identity, settings);
        var now = timeProvider.GetUtcNow();
        DnsCacheEntry entry;
        if (!forceRefresh && _cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
        {
            CacheHits.Add(1, new KeyValuePair<string, object?>("identity_id", identity.IdentityId));
            entry = cached;
        }
        else
        {
            CacheMisses.Add(1, new KeyValuePair<string, object?>("identity_id", identity.IdentityId));
            var created = new Lazy<Task<DnsCacheEntry>>(
                () => ResolveFlightAsync(identity, key), LazyThreadSafetyMode.ExecutionAndPublication);
            var flight = _flights.GetOrAdd(key, created);
            if (!ReferenceEquals(created, flight))
                SingleFlightJoins.Add(1,
                    new KeyValuePair<string, object?>("identity_id", identity.IdentityId));
            entry = await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        now = timeProvider.GetUtcNow();
        var readiness = policy.Evaluate(
            identity, local, entry.Ptr, entry.Forward, now,
            entry.LastKnownValidAtUtc, entry.LastKnownValidUntilUtc);
        Record(readiness, settings);
        return readiness;
    }

    public async Task<IReadOnlyList<OutboundIdentityDnsReadiness>> GetAllAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var identities = _options.Identities
            .Where(configured => configured.Enabled)
            .Select(configured => OutboundIdentityFactory.TryCreate(_options, configured, out var identity)
                ? identity : null)
            .Where(identity => identity is not null)
            .Cast<OutboundIdentity>()
            .OrderBy(identity => identity.IdentityId, StringComparer.Ordinal)
            .ToArray();
        using var gate = new SemaphoreSlim(Math.Max(1, _options.DnsReadiness.MaximumConcurrentLookups));
        var results = new OutboundIdentityDnsReadiness[identities.Length];
        await Task.WhenAll(identities.Select(async (identity, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await GetReadinessAsync(identity, forceRefresh, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return results;
    }

    private async Task<DnsCacheEntry> ResolveAsync(OutboundIdentity identity, string key)
    {
        var ptrTask = dns.ResolvePtrAsync(identity.Address, CancellationToken.None);
        var forwardTask = dns.ResolveIpv4Async(identity.ExpectedPtrHostName, CancellationToken.None);
        await Task.WhenAll(ptrTask, forwardTask).ConfigureAwait(false);
        var ptr = await ptrTask.ConfigureAwait(false);
        var forward = await forwardTask.ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        _cache.TryGetValue(key, out var prior);
        var syntheticLocal = new LocalOutboundIdentityBinding(
            identity.InterfaceName, true, true, true, identity.InterfaceName);
        var evaluated = policy.Evaluate(identity, syntheticLocal, ptr, forward, now,
            prior?.LastKnownValidAtUtc, prior?.LastKnownValidUntilUtc);
        var lastValid = evaluated.DnsState == ForwardConfirmedReverseDnsState.Valid
            ? now
            : prior?.LastKnownValidAtUtc;
        var lastValidUntil = evaluated.DnsState == ForwardConfirmedReverseDnsState.Valid
            ? evaluated.ExpiresAtUtc
            : prior?.LastKnownValidUntilUtc;
        var entry = new DnsCacheEntry(ptr, forward, evaluated.ExpiresAtUtc, lastValid, lastValidUntil);
        _cache[key] = entry;
        logger.LogInformation(
            "Outbound identity DNS readiness evaluated: {IdentityId} {SourceAddress} {InterfaceName} expected PTR {ExpectedPtrHostName} EHLO {EhloHostName} state {ReadinessState} mode {ValidationMode} evaluated {EvaluatedAtUtc} expires {ExpiresAtUtc} last valid {LastKnownValidAtUtc} policy {PolicyVersion}",
            identity.IdentityId, identity.Address, identity.InterfaceName,
            identity.ExpectedPtrHostName, identity.EhloHostName, evaluated.DnsState,
            _options.DnsReadiness.ValidationMode, evaluated.EvaluatedAtUtc, evaluated.ExpiresAtUtc,
            evaluated.LastKnownValidAtUtc, evaluated.ValidationPolicyVersion);
        return entry;
    }

    private async Task<DnsCacheEntry> ResolveFlightAsync(OutboundIdentity identity, string key)
    {
        try
        {
            return await ResolveAsync(identity, key).ConfigureAwait(false);
        }
        finally
        {
            _flights.TryRemove(key, out _);
        }
    }

    private static OutboundIdentityDnsReadiness Disabled(
        OutboundIdentity identity,
        LocalOutboundIdentityBinding local,
        DateTimeOffset now,
        string policyVersion)
    {
        var localState = !local.InterfaceExists || !local.InterfaceOperational ||
            local.AddressBound && !string.Equals(local.ActualInterfaceName, identity.InterfaceName, StringComparison.Ordinal)
                ? ForwardConfirmedReverseDnsState.WrongInterface
                : !local.AddressBound
                    ? local.ActualInterfaceName is null
                        ? ForwardConfirmedReverseDnsState.LocalAddressNotBound
                        : ForwardConfirmedReverseDnsState.WrongInterface
                    : ForwardConfirmedReverseDnsState.NotEvaluated;
        return new()
        {
            IdentityId = identity.IdentityId,
            Address = identity.Address,
            ExpectedHostName = identity.ExpectedPtrHostName,
            EhloHostName = identity.EhloHostName,
            State = localState,
            DnsState = ForwardConfirmedReverseDnsState.NotEvaluated,
            IsEligible = localState == ForwardConfirmedReverseDnsState.NotEvaluated,
            EvaluatedAtUtc = now,
            ExpiresAtUtc = now,
            ValidationPolicyVersion = policyVersion
        };
    }

    private static string CacheKey(
        OutboundIdentity identity,
        OutboundIdentityDnsReadinessOptions options) =>
        string.Join('|', identity.IdentityId.ToLowerInvariant(), identity.Address,
            identity.ExpectedPtrHostName, identity.EhloHostName, options.ValidationMode,
            options.ValidationPolicyVersion);

    private static void Record(
        OutboundIdentityDnsReadiness readiness,
        OutboundIdentityDnsReadinessOptions options)
    {
        Validations.Add(1,
            new("identity_id", readiness.IdentityId),
            new("readiness_state", readiness.State.ToString()),
            new("validation_mode", options.Mode.ToString()));
        if (readiness.DnsState == ForwardConfirmedReverseDnsState.Valid)
            Valid.Add(1, new KeyValuePair<string, object?>("identity_id", readiness.IdentityId));
        if (readiness.IsDegraded)
            LastKnownGoodUsed.Add(1,
                new KeyValuePair<string, object?>("identity_id", readiness.IdentityId));
    }

    private sealed record DnsCacheEntry(
        OutboundIdentityDnsQueryResult Ptr,
        OutboundIdentityDnsQueryResult Forward,
        DateTimeOffset ExpiresAtUtc,
        DateTimeOffset? LastKnownValidAtUtc,
        DateTimeOffset? LastKnownValidUntilUtc);
}

public sealed class InMemoryOutboundIdentityHealthStore(
    OutboundIdentityHealthPolicy policy,
    TimeProvider timeProvider) : IOutboundIdentityHealthStore
{
    private readonly ConcurrentDictionary<(string IdentityId, MailProvider Provider), OutboundIdentityHealth> _states = new();

    public Task<OutboundIdentityHealth> GetAsync(
        string identityId,
        MailProvider provider,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (identityId.ToLowerInvariant(), provider);
        var state = _states.GetOrAdd(key, _ => new(
            identityId, provider, OutboundIdentityHealthState.Healthy));
        if (state.State is OutboundIdentityHealthState.Cooldown or OutboundIdentityHealthState.Quarantined &&
            state.CooldownUntil <= timeProvider.GetUtcNow())
        {
            state = state with
            {
                State = OutboundIdentityHealthState.Healthy,
                CooldownUntil = null,
                AttributableFailureCount = 0,
                Reason = null
            };
            _states[key] = state;
        }
        return Task.FromResult(state);
    }

    public Task RecordAsync(OutboundIdentityOutcome outcome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = !outcome.Global &&
            outcome.CooldownScope is SmtpCooldownScope.OutboundIdentity or SmtpCooldownScope.SourceIp
            ? outcome.Provider
            : MailProvider.Unknown;
        var key = (outcome.IdentityId.ToLowerInvariant(), provider);
        _states.AddOrUpdate(key,
            _ => policy.Evaluate(outcome, provider, 0),
            (_, current) => policy.Evaluate(outcome, provider, current.AttributableFailureCount));
        return Task.CompletedTask;
    }
}

public sealed class OutboundIdentityHealthPolicy(
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider)
{
    private readonly OutboundIdentityOptions _options = options.Value.OutboundIdentities;

    public OutboundIdentityHealth Evaluate(
        OutboundIdentityOutcome outcome,
        MailProvider provider,
        int priorFailures)
    {
        if (outcome.HealthImpact == SmtpHealthImpact.Success)
            return new(outcome.IdentityId, provider, OutboundIdentityHealthState.Healthy);
        if (outcome.HealthImpact is SmtpHealthImpact.None or SmtpHealthImpact.TemporaryFailure)
            return new(outcome.IdentityId, provider, OutboundIdentityHealthState.Healthy,
                AttributableFailureCount: priorFailures);

        var failures = priorFailures + 1;
        if (outcome.HealthImpact == SmtpHealthImpact.Restriction)
        {
            var until = outcome.RetryAfter ?? timeProvider.GetUtcNow().AddMinutes(
                Math.Max(1, _options.PolicyBlockCooldownMinutes));
            return new(outcome.IdentityId, provider, OutboundIdentityHealthState.Cooldown,
                until, failures, outcome.Reason);
        }

        if (failures >= Math.Max(1, _options.QuarantineFailureThreshold))
            return new(outcome.IdentityId, provider, OutboundIdentityHealthState.Quarantined,
                timeProvider.GetUtcNow().AddMinutes(Math.Max(1, _options.QuarantineMinutes)),
                failures, outcome.Reason);
        return new(outcome.IdentityId, provider, OutboundIdentityHealthState.Degraded,
            AttributableFailureCount: failures, Reason: outcome.Reason);
    }
}

public sealed class SmtpConnectionFactory : ISmtpConnectionFactory
{
    public async Task<ISmtpConnection> ConnectAsync(
        string host,
        int port,
        IPAddress localAddress,
        CancellationToken cancellationToken = default)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            if (!localAddress.Equals(IPAddress.Any))
            {
                try
                {
                    client.Client.Bind(new IPEndPoint(localAddress, 0));
                }
                catch (SocketException exception)
                {
                    throw new OutboundIdentityBindException(localAddress, exception);
                }
            }
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new SmtpConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class SmtpConnection(TcpClient client) : ISmtpConnection
    {
        public Stream Stream { get; } = client.GetStream();
        public string LocalAddress { get; } =
            (client.Client.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class OutboundIdentityBindException(IPAddress address, SocketException innerException)
    : IOException($"The configured outbound source address '{address}' could not be bound.", innerException)
{
    public IPAddress Address { get; } = address;
}

public sealed class OutboundIdentityStartupValidator(
    IOptions<EmailValidationOptions> options,
    IForwardConfirmedReverseDnsValidator validator,
    TimeProvider timeProvider,
    ILogger<OutboundIdentityStartupValidator> logger) : BackgroundService
{
    private readonly OutboundIdentityOptions _options = options.Value.OutboundIdentities;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        var first = true;
        var previous = new Dictionary<string, ForwardConfirmedReverseDnsState>(StringComparer.OrdinalIgnoreCase);
        while (!stoppingToken.IsCancellationRequested)
        {
            var readiness = await validator.GetAllAsync(
                forceRefresh: true, stoppingToken).ConfigureAwait(false);
            foreach (var item in readiness)
            {
                if (first || !previous.TryGetValue(item.IdentityId, out var state) || state != item.State)
                    logger.LogInformation(
                        "Outbound identity readiness {IdentityId}: {ReadinessState}, eligible={Eligible}, expires={ExpiresAtUtc}",
                        item.IdentityId, item.State, item.IsEligible, item.ExpiresAtUtc);
                previous[item.IdentityId] = item.State;
            }
            first = false;
            var now = timeProvider.GetUtcNow();
            var refreshAhead = TimeSpan.FromMinutes(Math.Max(0, _options.DnsReadiness.RefreshAheadMinutes));
            var next = readiness.Count == 0
                ? now.AddMinutes(5)
                : readiness.Min(item => item.ExpiresAtUtc) - refreshAhead;
            var delay = next - now;
            if (delay < TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
            var jitter = Math.Clamp(_options.DnsReadiness.RefreshJitterPercent, 0, 50);
            if (jitter > 0)
                delay += TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Random.Shared.Next(0, jitter + 1) / 100d);
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }
}
