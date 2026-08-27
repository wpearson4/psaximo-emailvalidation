using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
}

public sealed class ForwardConfirmedReverseDnsValidator : IForwardConfirmedReverseDnsValidator
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public async Task<ForwardConfirmedReverseDnsState> ValidateAsync(
        OutboundIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(identity.IdentityId, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.State;
        var state = await ResolveAsync(identity, cancellationToken).ConfigureAwait(false);
        _cache[identity.IdentityId] = new(state, DateTimeOffset.UtcNow.AddMinutes(
            state == ForwardConfirmedReverseDnsState.LookupFailed ? 1 : 15));
        return state;
    }

    private static async Task<ForwardConfirmedReverseDnsState> ResolveAsync(
        OutboundIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            var reverse = await Dns.GetHostEntryAsync(identity.Address)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            var host = reverse.HostName.Trim().TrimEnd('.');
            if (host.Length == 0 || IPAddress.TryParse(host, out _))
                return ForwardConfirmedReverseDnsState.MissingPtr;
            if (!string.Equals(host, identity.EhloHostName.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                return ForwardConfirmedReverseDnsState.PtrMismatch;
            IPAddress[] forward;
            try
            {
                forward = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.HostNotFound)
            {
                return ForwardConfirmedReverseDnsState.MissingForwardRecord;
            }
            return forward.Contains(identity.Address)
                ? ForwardConfirmedReverseDnsState.Valid
                : ForwardConfirmedReverseDnsState.ForwardMismatch;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.HostNotFound)
        {
            return ForwardConfirmedReverseDnsState.MissingPtr;
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return ForwardConfirmedReverseDnsState.LookupFailed;
        }
    }

    private sealed record CacheEntry(ForwardConfirmedReverseDnsState State, DateTimeOffset ExpiresAt);
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
                client.Client.Bind(new IPEndPoint(localAddress, 0));
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

public sealed class OutboundIdentityStartupValidator(
    IOptions<EmailValidationOptions> options,
    IOutboundIdentitySelector selector,
    ILogger<OutboundIdentityStartupValidator> logger) : IHostedService
{
    private readonly OutboundIdentityOptions _options = options.Value.OutboundIdentities;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        foreach (var providerName in _options.ProviderGroups.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!Enum.TryParse<MailProvider>(providerName, true, out var provider)) continue;
            var result = await selector.SelectAsync(
                new("startup-validation.invalid", provider), cancellationToken).ConfigureAwait(false);
            if (!result.Selected)
                throw new InvalidOperationException(
                    $"Outbound identity group '{result.ProviderGroup}' cannot safely serve provider '{provider}' ({result.Reason}).");
            logger.LogInformation(
                "Outbound identity group {ProviderGroup} passed startup validation for {Provider}",
                result.ProviderGroup, provider);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
