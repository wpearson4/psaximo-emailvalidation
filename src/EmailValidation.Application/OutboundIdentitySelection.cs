using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Application;

public sealed class RendezvousOutboundIdentitySelector(
    IOptions<EmailValidationOptions> options,
    ILocalOutboundIdentityDiscovery discovery,
    IForwardConfirmedReverseDnsValidator fcrDnsValidator,
    IOutboundIdentityHealthStore healthStore,
    TimeProvider timeProvider,
    ILogger<RendezvousOutboundIdentitySelector> logger) : IOutboundIdentitySelector
{
    private static readonly Meter Meter = new("EmailValidation.OutboundIdentity", "1.0.0");
    private static readonly Counter<long> Selected = Meter.CreateCounter<long>("outbound_identity_selected_total");
    private static readonly Counter<long> Unavailable = Meter.CreateCounter<long>("outbound_identity_unavailable_total");
    private static readonly Counter<long> FcrDnsInvalid = Meter.CreateCounter<long>("outbound_identity_fcrdns_invalid_total");
    private readonly OutboundIdentityOptions _options = options.Value.OutboundIdentities;

    public async Task<OutboundIdentitySelectionResult> SelectAsync(
        OutboundIdentitySelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return Empty(OutboundIdentitySelectionReason.FeatureDisabled, string.Empty);

        var domain = NormalizeDomain(request.NormalizedRecipientDomain);
        if (!_options.ProviderGroups.TryGetValue(request.Provider.ToString(), out var group) ||
            string.IsNullOrWhiteSpace(group))
        {
            RecordUnavailable(request.Provider, OutboundIdentitySelectionReason.ProviderGroupNotConfigured);
            return Empty(OutboundIdentitySelectionReason.ProviderGroupNotConfigured, string.Empty);
        }
        if (!_options.IdentityGroups.TryGetValue(group, out var memberIds) || memberIds.Length == 0)
        {
            RecordUnavailable(request.Provider, OutboundIdentitySelectionReason.NoConfiguredIdentities);
            return Empty(OutboundIdentitySelectionReason.NoConfiguredIdentities, group);
        }

        var bound = await discovery.GetBoundIpv4AddressesAsync(
            _options.InterfaceName, cancellationToken).ConfigureAwait(false);
        var members = new HashSet<string>(memberIds, StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();
        var eligible = new List<OutboundIdentity>();
        foreach (var configured in _options.Identities
                     .Where(item => members.Contains(item.IdentityId))
                     .OrderBy(item => item.IdentityId, StringComparer.Ordinal))
        {
            if (!TryCreateIdentity(configured, out var identity) || !identity.Enabled ||
                !IsApprovedAddress(identity.Address) ||
                (_options.RequireAddressToBeBound && !bound.Contains(identity.Address.ToString())))
            {
                rejected.Add(configured.IdentityId);
                continue;
            }

            var fcrDns = await fcrDnsValidator.ValidateAsync(identity, cancellationToken).ConfigureAwait(false);
            identity = identity with { FcrDnsState = fcrDns };
            if (_options.RequireForwardConfirmedReverseDns && fcrDns != ForwardConfirmedReverseDnsState.Valid)
            {
                FcrDnsInvalid.Add(1,
                    new("provider", request.Provider.ToString()),
                    new("identity_id", identity.IdentityId),
                    new("fcrdns_state", fcrDns.ToString()));
                rejected.Add(identity.IdentityId);
                continue;
            }

            var now = timeProvider.GetUtcNow();
            var global = await healthStore.GetAsync(
                identity.IdentityId, MailProvider.Unknown, cancellationToken).ConfigureAwait(false);
            var provider = await healthStore.GetAsync(
                identity.IdentityId, request.Provider, cancellationToken).ConfigureAwait(false);
            if (!global.IsEligible(now) || !provider.IsEligible(now))
            {
                rejected.Add(identity.IdentityId);
                continue;
            }

            eligible.Add(identity);
        }

        if (eligible.Count == 0)
        {
            var reason = bound.Count == 0
                ? OutboundIdentitySelectionReason.NoLocallyBoundIdentities
                : OutboundIdentitySelectionReason.NoEligibleIdentities;
            logger.LogWarning(
                "No eligible outbound identity exists for provider {Provider} group {ProviderGroup}",
                request.Provider, group);
            RecordUnavailable(request.Provider, reason);
            return Empty(reason, group, rejected);
        }

        var selected = eligible
            .Select(identity => new
            {
                Identity = identity,
                Score = Score(_options.SelectionAlgorithmVersion, request.Provider, domain, identity.IdentityId)
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Identity.IdentityId, StringComparer.Ordinal)
            .First().Identity;
        Selected.Add(1,
            new("provider", request.Provider.ToString()),
            new("identity_id", selected.IdentityId),
            new("provider_group", group));
        return new(selected, OutboundIdentitySelectionReason.Selected, group,
            _options.SelectionAlgorithmVersion, rejected);
    }

    internal static ulong Score(
        string version,
        MailProvider provider,
        string normalizedDomain,
        string identityId)
    {
        var canonical = $"{version}|{provider}|{NormalizeDomain(normalizedDomain)}|{identityId.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    private bool TryCreateIdentity(
        OutboundIdentityConfiguration configured,
        out OutboundIdentity identity)
    {
        var interfaceName = string.IsNullOrWhiteSpace(configured.InterfaceName)
            ? _options.InterfaceName
            : configured.InterfaceName.Trim();
        if (string.IsNullOrWhiteSpace(configured.IdentityId) ||
            string.IsNullOrWhiteSpace(configured.EhloHostName) ||
            !IPAddress.TryParse(configured.Address, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !string.Equals(interfaceName, _options.InterfaceName, StringComparison.Ordinal))
        {
            identity = null!;
            return false;
        }

        identity = new OutboundIdentity
        {
            IdentityId = configured.IdentityId.Trim(),
            Address = address,
            InterfaceName = interfaceName,
            EhloHostName = configured.EhloHostName.Trim().TrimEnd('.').ToLowerInvariant(),
            Enabled = configured.Enabled,
            FcrDnsState = ForwardConfirmedReverseDnsState.NotEvaluated
        };
        return true;
    }

    private bool IsApprovedAddress(IPAddress address)
    {
        if (!Ipv4Cidr.TryParse(_options.AllowedCidr, out var cidr) || !cidr.Contains(address)) return false;
        var value = Ipv4Cidr.ToUInt32(address);
        if (value == cidr.Network || value == cidr.Broadcast) return false;
        return !IPAddress.TryParse(_options.GatewayAddress, out var gateway) || !address.Equals(gateway);
    }

    private OutboundIdentitySelectionResult Empty(
        OutboundIdentitySelectionReason reason,
        string group,
        IReadOnlyList<string>? rejected = null) =>
        new(null, reason, group, _options.SelectionAlgorithmVersion, rejected ?? []);

    private static void RecordUnavailable(
        MailProvider provider,
        OutboundIdentitySelectionReason reason) =>
        Unavailable.Add(1,
            new("provider", provider.ToString()),
            new("reason", reason.ToString()));

    private static string NormalizeDomain(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();
}

internal readonly record struct Ipv4Cidr(uint Network, uint Broadcast)
{
    public bool Contains(IPAddress address)
    {
        var value = ToUInt32(address);
        return value >= Network && value <= Broadcast;
    }

    public static bool TryParse(string value, out Ipv4Cidr cidr)
    {
        cidr = default;
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32) return false;
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = ToUInt32(address) & mask;
        cidr = new(network, network | ~mask);
        return true;
    }

    public static uint ToUInt32(IPAddress address) =>
        BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
}
