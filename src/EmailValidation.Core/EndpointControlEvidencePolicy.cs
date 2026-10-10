using System.Security.Cryptography;
using System.Text;

namespace EmailValidation.Core;

/// <summary>Randomized controls describe one public endpoint, never every MX for a domain.</summary>
public static class EndpointControlEvidencePolicy
{
    public static string TopologyFingerprint(DomainIntelligence domain) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', domain.MxRecords
            .OrderBy(x => x.Preference).ThenBy(x => SmtpRecipientEvidencePolicy.NormalizeHost(x.Host), StringComparer.Ordinal)
            .Select(x => $"{x.Preference}:{SmtpRecipientEvidencePolicy.NormalizeHost(x.Host)}")))));

    public static string ScopeFingerprint(ControlEvidenceScope scope) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{scope.MxHost}|{scope.Preference}|{scope.Provider}|{scope.GatewayProvider}|{scope.TopologyFingerprint}|{scope.StrategyVersion}")));

    public static bool IsCompatible(DomainIntelligence domain, SmtpProbeResult target, TimeSpan? correlationWindow = null)
    {
        var controls = domain.CatchAll;
        var scope = controls.ControlScope;
        var targetAt = SmtpRecipientEvidencePolicy.RecipientObservedAt(target);
        var window = correlationWindow ?? TimeSpan.FromMinutes(5);
        return scope is not null && !string.IsNullOrWhiteSpace(scope.AcquisitionId) &&
            controls.EvidenceContractVersion == CatchAllDetectionResult.CurrentRecipientBehaviorEvidenceContractVersion &&
            scope.Provider == domain.Provider.Provider && scope.GatewayProvider == domain.Provider.GatewayProvider &&
            scope.StrategyVersion == domain.StrategyVersion && scope.TopologyFingerprint == TopologyFingerprint(domain) &&
            domain.MxRecords.Any(mx => mx.Preference == scope.Preference &&
                SmtpRecipientEvidencePolicy.NormalizeHost(mx.Host) == scope.MxHost) &&
            SmtpRecipientEvidencePolicy.MxHost(target) == scope.MxHost &&
            controls.ObservedAt is { } controlAt && targetAt is { } observedAt &&
            observedAt >= controlAt && observedAt - controlAt <= window &&
            (controls.EvidenceExpiresAt is null || observedAt < controls.EvidenceExpiresAt) &&
            controls.Probes > 0 && controls.ProbeResults.Count == controls.Probes &&
            controls.ProbeResults.All(probe =>
                SmtpRecipientEvidencePolicy.MxHost(probe) == scope.MxHost &&
                SmtpRecipientEvidencePolicy.RecipientObservedAt(probe) is { } probeAt &&
                probeAt <= controlAt && observedAt - probeAt <= window);
    }

    public static bool HasRecipientSpecificControls(DomainIntelligence domain, SmtpProbeResult target) =>
        domain.CatchAll.EffectiveRecipientBehavior == DomainRecipientBehavior.RecipientSpecific &&
        IsCompatible(domain, target) &&
        domain.CatchAll.ProbeResults.All(SmtpRecipientEvidencePolicy.HasStrongRecipientRejection);

    public static CatchAllDetectionResult Inconclusive(CatchAllDetectionResult controls) => controls with
    {
        Status = CatchAllStatus.Unknown,
        RecipientBehavior = DomainRecipientBehavior.Unknown,
        ReasonCode = CatchAllReasonCode.EndpointEvidenceMismatch,
        Confidence = 0.20,
        IndependentObservationCount = 0,
        Detail = "Randomized controls do not provide fresh, compatible recipient evidence on the target MX endpoint.",
        RefreshInconclusive = true
    };
}
