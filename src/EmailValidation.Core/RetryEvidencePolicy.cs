namespace EmailValidation.Core;

/// <summary>Observation clocks, rather than cache access or dispatch time, govern retry budgets.</summary>
public static class RetryEvidencePolicy
{
    public static DateTimeOffset LatestObservation(EmailValidationResult result, DateTimeOffset fallback) =>
        new DateTimeOffset?[]
        {
            fallback, result.Metadata?.ValidatedAt, result.MailboxEvidenceObservedAt,
            result.DomainIntelligence?.RoutingEvidence?.ObservedAt,
            result.DomainIntelligence?.MailRouting?.ObservedAtUtc,
            result.CatchAllEvidence?.ObservedAt, result.CatchAllEvidence?.RefreshAttemptedAt
        }.Where(value => value.HasValue).Max(value => value!.Value);

    public static bool HasNewObservation(EmailValidationResult previous, EmailValidationResult current, DateTimeOffset after)
    {
        if (current.ProbeAttempted && current.MailboxEvidenceObservedAt > after) return true;
        var routingObservedAt = current.DomainIntelligence?.RoutingEvidence?.ObservedAt ??
            current.DomainIntelligence?.MailRouting?.ObservedAtUtc;
        if (routingObservedAt > after && (previous.DomainIntelligence?.Dns.IsTransient == true ||
            previous.ReasonCodes.Any(reason => reason is ReasonCode.DnsTimeout or ReasonCode.DnsFailure) ||
            current.DomainIntelligence?.Dns.HasDefinitiveNoRoute == true ||
            current.DomainIntelligence?.Dns.Status == DnsStatus.DomainNotFound)) return true;
        var behavior = current.CatchAllEvidence;
        return behavior?.RefreshAttemptedAt > after && behavior.ProbeResults.Any(probe => probe.ProbeAttempted);
    }
}
