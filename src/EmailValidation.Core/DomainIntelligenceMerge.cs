namespace EmailValidation.Core;

/// <summary>Component observations, not save time, determine which facts survive a concurrent write.</summary>
public static class DomainIntelligenceMerge
{
    public static DomainIntelligence Merge(DomainIntelligence? current, DomainIntelligence incoming)
    {
        if (current is null) return incoming with { ProfileVersion = 1 };
        var routingNewer = RoutingAt(incoming) > RoutingAt(current);
        var routing = routingNewer ? incoming : current;
        var other = routingNewer ? current : incoming;
        var compatible = Compatible(routing, other);
        var transition = routingNewer && !compatible;
        var topologySince = transition ? RoutingAt(incoming) : current.TopologyChangedAt;
        var behavior = routing;
        if (compatible && BehaviorAt(other) > BehaviorAt(routing)) behavior = other;
        else if (compatible && BehaviorAt(other) == BehaviorAt(routing) &&
            other.CatchAll.RefreshAttemptedAt > routing.CatchAll.RefreshAttemptedAt)
            behavior = other; // Preserve refresh backoff without promoting its clock to an observation.
        // An old snapshot from an earlier visit to the same MX topology cannot revive its controls.
        var behaviorValid = topologySince is null || behavior.CatchAll.ObservedAt >= topologySince;
        var behaviorConflict = compatible && BehaviorAt(current) != DateTimeOffset.MinValue &&
            BehaviorAt(current) == BehaviorAt(incoming) &&
            (current.CatchAll.RoutingAttestation is null || current.CatchAll.RoutingAttestation != incoming.CatchAll.RoutingAttestation) &&
            current.CatchAll.EffectiveRecipientBehavior != incoming.CatchAll.EffectiveRecipientBehavior;
        var authentication = (incoming.AuthenticationEvidence?.ObservedAt ?? incoming.Authentication.ObservedAtUtc) >
            (current.AuthenticationEvidence?.ObservedAt ?? current.Authentication.ObservedAtUtc) ? incoming : current;
        var provider = compatible && ProviderAt(other) > ProviderAt(routing) ? other : routing;
        return routing with
        {
            ProfileVersion = checked(current.ProfileVersion + 1),
            TopologyChangedAt = topologySince,
            Provider = provider.Provider, ProviderEvidence = provider.ProviderEvidence,
            ProviderFingerprint = provider.ProviderFingerprint,
            Authentication = authentication.Authentication,
            AuthenticationEvidence = authentication.AuthenticationEvidence,
            AuthenticationFingerprint = authentication.AuthenticationFingerprint,
            CatchAll = behaviorConflict
                ? new(CatchAllStatus.Unknown, 0, 0, 0, 0, "Concurrent recipient evidence disagrees at the same observation time.")
                    { ObservedAt = BehaviorAt(behavior), ReasonCode = CatchAllReasonCode.MixedOrInconclusive, EvidenceExpiresAt = BehaviorAt(behavior) }
                : behaviorValid ? behavior.CatchAll : new(CatchAllStatus.NotAttempted, 0, 0, 0, 0),
            Behavior = behaviorValid && !behaviorConflict ? behavior.Behavior : null,
            CatchAllFingerprint = behaviorValid && !behaviorConflict ? behavior.CatchAllFingerprint : null,
            FirstObservedUtc = current.FirstObservedUtc == default ? current.ObservedAt : current.FirstObservedUtc,
            LastObservedUtc = incoming.LastObservedUtc > current.LastObservedUtc ? incoming.LastObservedUtc : current.LastObservedUtc,
            LastChangedUtc = transition ? RoutingAt(incoming) : current.LastChangedUtc,
            ChangeCount = transition ? checked(current.ChangeCount + 1) : current.ChangeCount
        };
    }

    public static DateTimeOffset RoutingAt(DomainIntelligence value) => value.RoutingEvidence?.ObservedAt ?? value.ObservedAt;
    private static DateTimeOffset ProviderAt(DomainIntelligence value) => value.ProviderEvidence?.ObservedAt ?? RoutingAt(value);
    private static DateTimeOffset BehaviorAt(DomainIntelligence value) =>
        new[] { value.CatchAll.ObservedAt, value.CatchAll.BehaviorEvaluatedAt }
            .Max() ?? DateTimeOffset.MinValue;
    public static bool Compatible(DomainIntelligence left, DomainIntelligence right) =>
        string.Equals(Topology(left), Topology(right), StringComparison.Ordinal) &&
        left.Provider.Provider == right.Provider.Provider && left.Provider.GatewayProvider == right.Provider.GatewayProvider &&
        left.StrategyVersion == right.StrategyVersion &&
        left.IntelligencePolicyVersion == right.IntelligencePolicyVersion;
    private static string Topology(DomainIntelligence value) =>
        $"{value.Dns.Status}:{value.Dns.ExplicitNullMx}:{value.Dns.UsedAddressFallback}|" +
        string.Join('|', value.MxRecords.OrderBy(x => x.Preference)
            .ThenBy(x => x.Host, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Preference}:{x.Host.TrimEnd('.').ToLowerInvariant()}"));
}
