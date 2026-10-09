namespace EmailValidation.Core;

public static class DomainEvidenceFreshness
{
    public static DateTimeOffset ExpiresAt(DomainIntelligence intelligence, DomainIntelligenceOptions options)
    {
        var routing = intelligence.RoutingEvidence?.ExpiresAt;
        if (routing is null)
        {
            // Pre-clock records may contain a storage-renewed expiry. Bound them
            // by the original observation rather than treating that expiry as authority.
            var observed = intelligence.MailRouting?.ObservedAtUtc ?? intelligence.ObservedAt;
            var lifetime = intelligence.Dns.IsTransient
                ? TimeSpan.FromSeconds(Math.Max(0, options.TransientDnsFreshnessSeconds))
                : intelligence.Dns.TimeToLive ?? TimeSpan.FromSeconds(Math.Max(0, options.MissingRoutingTtlSeconds));
            routing = observed.Add(lifetime < TimeSpan.Zero ? TimeSpan.Zero : lifetime);
        }
        return new DateTimeOffset?[]
        {
            routing, intelligence.EvidenceExpiresAt,
            intelligence.ProviderEvidence?.ExpiresAt, intelligence.AuthenticationEvidence?.ExpiresAt
        }.Where(value => value.HasValue).Min(value => value!.Value);
    }
}
