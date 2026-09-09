using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

/// <summary>Checks the sender domains owned by the configured outbound identities.</summary>
public sealed class OutboundIdentityProbeSenderHealthChecker(
    IEmailNormalizer normalizer,
    IDnsMailResolver dnsResolver,
    IOptions<EmailValidationOptions> options) : IProbeSenderHealthChecker
{
    public async Task<ProbeSenderHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        var identities = options.Value.OutboundIdentities.Identities
            .Where(identity => identity.Enabled)
            .ToArray();
        if (!options.Value.OutboundIdentities.Enabled || identities.Length == 0)
            return new(ProbeSenderHealthStatus.NotConfigured, null, null,
                "No enabled outbound identities have been configured.");

        var sendersByDomain = new Dictionary<string, (string Sender, string IdentityId)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var configured in identities)
        {
            if (!OutboundIdentityProbeSender.TryNormalize(configured.ProbeSenderAddress, out var sender))
                return new(ProbeSenderHealthStatus.InvalidSyntax, configured.ProbeSenderAddress, null,
                    $"Outbound identity '{configured.IdentityId}' has an invalid probe sender address.");

            var normalized = normalizer.Normalize(sender);
            if (!normalized.IsValid || normalized.Domain is null)
                return new(ProbeSenderHealthStatus.InvalidSyntax, sender, null,
                    $"Outbound identity '{configured.IdentityId}' has an invalid probe sender address.");

            sendersByDomain.TryAdd(normalized.Domain, (sender, configured.IdentityId));
        }

        ProbeSenderHealth? representative = null;
        foreach (var (domain, configured) in sendersByDomain)
        {
            var dns = await dnsResolver.ResolveAsync(domain, cancellationToken).ConfigureAwait(false);
            var health = dns.Status switch
            {
                DnsStatus.DomainNotFound when !dns.DomainExists => new ProbeSenderHealth(
                    ProbeSenderHealthStatus.DomainNotFound, configured.Sender, domain,
                    $"The sender domain for outbound identity '{configured.IdentityId}' does not exist."),
                DnsStatus.Timeout or DnsStatus.Failure => new ProbeSenderHealth(
                    ProbeSenderHealthStatus.DnsUnavailable, configured.Sender, domain,
                    $"The sender domain for outbound identity '{configured.IdentityId}' could not be resolved."),
                _ when dns.Status == DnsStatus.DomainNotFound || !dns.DomainExists => new ProbeSenderHealth(
                    ProbeSenderHealthStatus.DomainNotFound, configured.Sender, domain,
                    $"The sender domain for outbound identity '{configured.IdentityId}' does not exist."),
                _ when !dns.MxRecords.Any() => new ProbeSenderHealth(
                    ProbeSenderHealthStatus.NoMailRouting, configured.Sender, domain,
                    $"The sender domain for outbound identity '{configured.IdentityId}' has no MX routing."),
                _ => new ProbeSenderHealth(
                    ProbeSenderHealthStatus.Valid, configured.Sender, domain,
                    "All configured outbound identity sender domains have valid MX routing.")
            };
            if (!health.IsOperational)
                return health;
            representative ??= health;
        }

        return representative!;
    }
}
