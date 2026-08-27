using System.Globalization;
using System.Net;
using System.Net.Sockets;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Application;

public static class OutboundIdentityHostName
{
    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace)) return false;
        var candidate = value.EndsWith('.') ? value[..^1] : value;
        if (candidate.Length is 0 or > 253 || candidate.EndsWith('.') ||
            candidate.Contains('_', StringComparison.Ordinal) || IPAddress.TryParse(candidate, out _)) return false;
        var labels = candidate.Split('.');
        if (labels.Length < 2) return false;
        try
        {
            var asciiLabels = labels.Select(label => Idn.GetAscii(label)).ToArray();
            if (asciiLabels.Any(label => label.Length is 0 or > 63 ||
                    !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]) ||
                    label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
                return false;
            normalized = string.Join('.', asciiLabels).ToLowerInvariant();
            return normalized.Length <= 253;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public static class OutboundIdentityFactory
{
    public static bool TryCreate(
        OutboundIdentityOptions options,
        OutboundIdentityConfiguration configured,
        out OutboundIdentity identity)
    {
        var interfaceName = string.IsNullOrWhiteSpace(configured.InterfaceName)
            ? options.InterfaceName
            : configured.InterfaceName.Trim();
        if (string.IsNullOrWhiteSpace(configured.IdentityId) ||
            !IPAddress.TryParse(configured.Address, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !string.Equals(interfaceName, options.InterfaceName, StringComparison.Ordinal) ||
            !OutboundIdentityHostName.TryNormalize(configured.ExpectedPtrHostName, out var expected) ||
            !OutboundIdentityHostName.TryNormalize(configured.EhloHostName, out var ehlo))
        {
            identity = null!;
            return false;
        }

        identity = new()
        {
            IdentityId = configured.IdentityId.Trim(),
            Address = address,
            InterfaceName = interfaceName,
            ExpectedPtrHostName = expected,
            EhloHostName = ehlo,
            Enabled = configured.Enabled,
            FcrDnsState = ForwardConfirmedReverseDnsState.NotEvaluated
        };
        return true;
    }
}

public sealed class OutboundIdentityReadinessPolicy(IOptions<EmailValidationOptions> options)
{
    private readonly OutboundIdentityDnsReadinessOptions _options = options.Value.OutboundIdentities.DnsReadiness;

    public OutboundIdentityDnsReadiness Evaluate(
        OutboundIdentity identity,
        LocalOutboundIdentityBinding local,
        OutboundIdentityDnsQueryResult reverseResult,
        OutboundIdentityDnsQueryResult forward,
        DateTimeOffset evaluatedAtUtc,
        DateTimeOffset? lastKnownValidAtUtc = null,
        DateTimeOffset? lastKnownValidUntilUtc = null)
    {
        var ptrNames = NormalizePtrNames(reverseResult.HostNames, out var ptrNamesValid);
        var addresses = forward.Addresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
        var warnings = new List<ForwardConfirmedReverseDnsState>();
        var dnsState = DnsState(identity, reverseResult, forward, ptrNames, ptrNamesValid, addresses, warnings);
        var lastValid = dnsState == ForwardConfirmedReverseDnsState.Valid
            ? evaluatedAtUtc
            : lastKnownValidAtUtc;
        var degraded = false;
        var dnsEligible = dnsState == ForwardConfirmedReverseDnsState.Valid;
        if (dnsState is ForwardConfirmedReverseDnsState.DnsTemporaryFailure or
            ForwardConfirmedReverseDnsState.DnsResolverUnavailable &&
            _options.AllowLastKnownGoodOnTransientFailure && lastKnownValidAtUtc is { } previous &&
            (lastKnownValidUntilUtc ?? previous).AddMinutes(
                Math.Max(0, _options.LastKnownGoodGraceMinutes)) > evaluatedAtUtc)
        {
            dnsEligible = true;
            degraded = true;
        }

        var state = LocalState(local);
        if (state == ForwardConfirmedReverseDnsState.Valid &&
            _options.RequireEhloMatch && !string.Equals(
                identity.ExpectedPtrHostName, identity.EhloHostName, StringComparison.OrdinalIgnoreCase))
            state = ForwardConfirmedReverseDnsState.EhloMismatch;
        if (state == ForwardConfirmedReverseDnsState.Valid) state = dnsState;

        var localEligible = local.InterfaceExists && local.InterfaceOperational && local.AddressBound &&
            string.Equals(local.ActualInterfaceName, identity.InterfaceName, StringComparison.Ordinal);
        var dnsRequired = _options.Enabled && _options.Mode == OutboundIdentityDnsReadinessMode.Enforced;
        var eligible = localEligible && (!dnsRequired || dnsEligible) &&
            (!_options.RequireEhloMatch || string.Equals(
                identity.ExpectedPtrHostName, identity.EhloHostName, StringComparison.OrdinalIgnoreCase));
        var expires = Expiration(dnsState, reverseResult.TimeToLive, forward.TimeToLive, evaluatedAtUtc);
        if (degraded && lastKnownValidAtUtc is { } validAt)
            expires = Min(expires, (lastKnownValidUntilUtc ?? validAt).AddMinutes(
                Math.Max(0, _options.LastKnownGoodGraceMinutes)));

        return new()
        {
            IdentityId = identity.IdentityId,
            Address = identity.Address,
            ExpectedHostName = identity.ExpectedPtrHostName,
            EhloHostName = identity.EhloHostName,
            State = state,
            DnsState = dnsState,
            IsEligible = eligible,
            IsDegraded = degraded,
            PtrHostNames = ptrNames,
            ForwardAddresses = addresses,
            Warnings = warnings,
            PtrTtl = reverseResult.TimeToLive,
            ForwardTtl = forward.TimeToLive,
            EvaluatedAtUtc = evaluatedAtUtc,
            ExpiresAtUtc = expires,
            LastKnownValidAtUtc = lastValid,
            ValidationPolicyVersion = _options.ValidationPolicyVersion
        };
    }

    private ForwardConfirmedReverseDnsState DnsState(
        OutboundIdentity identity,
        OutboundIdentityDnsQueryResult reverseResult,
        OutboundIdentityDnsQueryResult forward,
        string[] ptrNames,
        bool ptrNamesValid,
        IPAddress[] addresses,
        List<ForwardConfirmedReverseDnsState> warnings)
    {
        if (!OutboundIdentityHostName.TryNormalize(identity.ExpectedPtrHostName, out var expected) ||
            !OutboundIdentityHostName.TryNormalize(identity.EhloHostName, out _))
            return ForwardConfirmedReverseDnsState.InvalidHostname;
        if (reverseResult.Status == OutboundIdentityDnsQueryStatus.ResolverUnavailable ||
            forward.Status == OutboundIdentityDnsQueryStatus.ResolverUnavailable)
            return ForwardConfirmedReverseDnsState.DnsResolverUnavailable;
        if (reverseResult.Status is OutboundIdentityDnsQueryStatus.TemporaryFailure or
                OutboundIdentityDnsQueryStatus.MalformedResponse ||
            forward.Status is OutboundIdentityDnsQueryStatus.TemporaryFailure or
                OutboundIdentityDnsQueryStatus.MalformedResponse)
            return ForwardConfirmedReverseDnsState.DnsTemporaryFailure;
        if (_options.RequireExpectedPtr &&
            (reverseResult.Status is OutboundIdentityDnsQueryStatus.NotFound or OutboundIdentityDnsQueryStatus.NoData ||
             ptrNames.Length == 0))
            return ForwardConfirmedReverseDnsState.MissingPtr;
        if (!ptrNamesValid) return ForwardConfirmedReverseDnsState.InvalidHostname;
        if (_options.RequireExpectedPtr)
        {
            if (_options.ValidationMode == ForwardConfirmedReverseDnsValidationMode.StrictOneToOne && ptrNames.Length != 1)
                return ForwardConfirmedReverseDnsState.MultiplePtrRecords;
            if (!ptrNames.Contains(expected, StringComparer.OrdinalIgnoreCase))
                return ForwardConfirmedReverseDnsState.UnexpectedPtr;
            if (ptrNames.Length > 1) warnings.Add(ForwardConfirmedReverseDnsState.MultiplePtrRecords);
        }
        if (_options.RequireForwardConfirmation &&
            (forward.Status is OutboundIdentityDnsQueryStatus.NotFound or OutboundIdentityDnsQueryStatus.NoData ||
             addresses.Length == 0))
            return ForwardConfirmedReverseDnsState.MissingForwardRecord;
        if (_options.RequireForwardConfirmation)
        {
            if (_options.ValidationMode == ForwardConfirmedReverseDnsValidationMode.StrictOneToOne && addresses.Length != 1)
                return ForwardConfirmedReverseDnsState.MultipleForwardAddresses;
            if (!addresses.Contains(identity.Address))
                return ForwardConfirmedReverseDnsState.ForwardAddressMismatch;
            if (addresses.Length > 1) warnings.Add(ForwardConfirmedReverseDnsState.MultipleForwardAddresses);
        }
        return ForwardConfirmedReverseDnsState.Valid;
    }

    private static string[] NormalizePtrNames(
        IReadOnlyList<string> names,
        out bool valid)
    {
        valid = true;
        var normalized = new List<string>();
        foreach (var name in names)
        {
            if (!OutboundIdentityHostName.TryNormalize(name, out var value))
            {
                valid = false;
                continue;
            }
            if (!normalized.Contains(value, StringComparer.OrdinalIgnoreCase)) normalized.Add(value);
        }
        return normalized.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static ForwardConfirmedReverseDnsState LocalState(LocalOutboundIdentityBinding local)
    {
        if (!local.InterfaceExists || !local.InterfaceOperational)
            return ForwardConfirmedReverseDnsState.WrongInterface;
        if (local.AddressBound && !string.Equals(
                local.ActualInterfaceName, local.ExpectedInterfaceName, StringComparison.Ordinal))
            return ForwardConfirmedReverseDnsState.WrongInterface;
        if (!local.AddressBound)
            return local.ActualInterfaceName is null
                ? ForwardConfirmedReverseDnsState.LocalAddressNotBound
                : ForwardConfirmedReverseDnsState.WrongInterface;
        return ForwardConfirmedReverseDnsState.Valid;
    }

    private DateTimeOffset Expiration(
        ForwardConfirmedReverseDnsState state,
        TimeSpan? ptrTtl,
        TimeSpan? forwardTtl,
        DateTimeOffset now)
    {
        if (state is ForwardConfirmedReverseDnsState.DnsTemporaryFailure or
            ForwardConfirmedReverseDnsState.DnsResolverUnavailable)
            return now.AddSeconds(Math.Max(15, _options.TransientFailureRetrySeconds));
        if (state != ForwardConfirmedReverseDnsState.Valid)
            return now.AddMinutes(Math.Max(1, _options.NegativeCacheMinutes));
        var available = new[] { ptrTtl, forwardTtl }.Where(value => value is not null).Select(value => value!.Value).ToArray();
        var ttl = available.Length == 0
            ? TimeSpan.FromMinutes(Math.Max(1, _options.FallbackFreshnessMinutes))
            : available.Min();
        var minimum = TimeSpan.FromMinutes(Math.Max(1, _options.MinimumFreshnessMinutes));
        var maximum = TimeSpan.FromHours(Math.Max(1, _options.MaximumFreshnessHours));
        return now.Add(ttl < minimum ? minimum : ttl > maximum ? maximum : ttl);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}
