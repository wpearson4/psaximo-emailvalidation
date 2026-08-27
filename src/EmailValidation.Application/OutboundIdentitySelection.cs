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
    private static readonly Counter<long> DnsReadinessExcluded =
        Meter.CreateCounter<long>("outbound_identity_excluded_dns_readiness_total");
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

        var members = new HashSet<string>(memberIds, StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();
        var eligible = new List<OutboundIdentity>();
        var readinessByIdentity = new Dictionary<string, OutboundIdentityDnsReadiness>(
            StringComparer.OrdinalIgnoreCase);
        var localRejections = 0;
        var dnsRejections = 0;
        var configurationRejections = 0;
        foreach (var configured in _options.Identities
                     .Where(item => members.Contains(item.IdentityId))
                     .OrderBy(item => item.IdentityId, StringComparer.Ordinal))
        {
            if (!OutboundIdentityFactory.TryCreate(_options, configured, out var identity) ||
                !identity.Enabled || !IsApprovedAddress(identity.Address))
            {
                configurationRejections++;
                rejected.Add(configured.IdentityId);
                continue;
            }

            var local = await discovery.InspectAsync(
                identity.InterfaceName, identity.Address, cancellationToken).ConfigureAwait(false);
            if (!local.InterfaceExists || !local.InterfaceOperational || !local.AddressBound ||
                !string.Equals(local.ActualInterfaceName, identity.InterfaceName, StringComparison.Ordinal))
            {
                localRejections++;
                rejected.Add(identity.IdentityId);
                continue;
            }

            var readiness = await fcrDnsValidator.GetReadinessAsync(
                identity, false, cancellationToken).ConfigureAwait(false);
            readinessByIdentity[identity.IdentityId] = readiness;
            identity = identity with { FcrDnsState = readiness.DnsState, DnsReadiness = readiness };
            if (!readiness.IsEligible)
            {
                FcrDnsInvalid.Add(1,
                    new("provider", request.Provider.ToString()),
                    new("identity_id", identity.IdentityId),
                    new("fcrdns_state", readiness.State.ToString()));
                DnsReadinessExcluded.Add(1,
                    new("provider", request.Provider.ToString()),
                    new("identity_id", identity.IdentityId),
                    new("readiness_state", readiness.State.ToString()));
                if (readiness.State is ForwardConfirmedReverseDnsState.LocalAddressNotBound or
                    ForwardConfirmedReverseDnsState.WrongInterface)
                    localRejections++;
                else
                    dnsRejections++;
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
            var reason = localRejections > 0 && localRejections + configurationRejections == rejected.Count
                ? OutboundIdentitySelectionReason.NoLocallyBoundIdentities
                : dnsRejections > 0 && dnsRejections + configurationRejections == rejected.Count
                    ? OutboundIdentitySelectionReason.NoDnsReadyIdentities
                    : configurationRejections == rejected.Count
                        ? OutboundIdentitySelectionReason.InvalidIdentityConfiguration
                        : OutboundIdentitySelectionReason.NoEligibleIdentities;
            logger.LogWarning(
                "No eligible outbound identity exists for provider {Provider} group {ProviderGroup}",
                request.Provider, group);
            RecordUnavailable(request.Provider, reason);
            return Empty(reason, group, rejected, readinessByIdentity);
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
        return new OutboundIdentitySelectionResult(selected, OutboundIdentitySelectionReason.Selected, group,
            _options.SelectionAlgorithmVersion, rejected)
        {
            Readiness = readinessByIdentity
        };
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
        IReadOnlyList<string>? rejected = null,
        IReadOnlyDictionary<string, OutboundIdentityDnsReadiness>? readiness = null) =>
        new OutboundIdentitySelectionResult(
            null, reason, group, _options.SelectionAlgorithmVersion, rejected ?? [])
        {
            Readiness = readiness ?? new Dictionary<string, OutboundIdentityDnsReadiness>(
                StringComparer.OrdinalIgnoreCase)
        };

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
