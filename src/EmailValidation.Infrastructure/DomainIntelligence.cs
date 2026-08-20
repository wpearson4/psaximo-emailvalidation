using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class DisposableEmailDetector(IOptions<EmailValidationOptions> options) :
    IDisposableEmailDetector,
    IDisposableDomainIntelligenceProvider
{
    private readonly HashSet<string> _domains = new(
        options.Value.Intelligence.DisposableDomains,
        StringComparer.OrdinalIgnoreCase);

    public bool IsDisposable(string domain) => MatchesDomainOrParent(_domains, domain);

    public DisposableDomainResult Evaluate(string domain) => IsDisposable(domain)
        ? new DisposableDomainResult(
            DisposableDomainStatus.KnownDisposable,
            0.99,
            EvidenceSource.ConfiguredIntelligenceProvider)
        : DisposableDomainResult.Unknown;

    internal static bool MatchesDomainOrParent(IReadOnlySet<string> domains, string domain)
    {
        var normalized = domain.Trim().TrimEnd('.');
        return domains.Any(candidate =>
            normalized.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith('.' + candidate, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class RoleAccountDetector(IOptions<EmailValidationOptions> options) : IRoleAccountDetector
{
    private readonly HashSet<string> _roles = new(
        options.Value.Intelligence.RoleAccounts,
        StringComparer.OrdinalIgnoreCase);

    public bool IsRoleAccount(string localPart)
    {
        var plus = localPart.IndexOf('+');
        var canonical = plus > 0 ? localPart[..plus] : localPart;
        return _roles.Contains(canonical);
    }
}

public sealed class MailProviderDetector : IMailProviderDetector
{
    private sealed record MxFingerprint(
        string Suffix,
        MailProvider Provider,
        ProviderFamily Family,
        GatewayProvider Gateway,
        double Confidence);

    // These are public SMTP boundaries, not backend-discovery hints. Keep all MX
    // fingerprints here so provider recognition remains conservative and testable.
    private static readonly MxFingerprint[] Fingerprints =
    [
        new("mail.protection.outlook.com", MailProvider.Microsoft365, ProviderFamily.Microsoft365,
            GatewayProvider.MicrosoftExchangeOnlineProtection, 0.99),
        new("olc.protection.outlook.com", MailProvider.Microsoft365, ProviderFamily.Microsoft365,
            GatewayProvider.MicrosoftExchangeOnlineProtection, 0.99),
        new("mx.microsoft", MailProvider.Microsoft365, ProviderFamily.Microsoft365,
            GatewayProvider.MicrosoftExchangeOnlineProtection, 0.99),
        new("google.com", MailProvider.GoogleWorkspace, ProviderFamily.GoogleWorkspace,
            GatewayProvider.GoogleWorkspace, 0.99),
        new("googlemail.com", MailProvider.GoogleWorkspace, ProviderFamily.GoogleWorkspace,
            GatewayProvider.GoogleWorkspace, 0.99),
        new("yahoodns.net", MailProvider.Yahoo, ProviderFamily.Yahoo,
            GatewayProvider.GenericSmtp, 0.99),
        new("pphosted.com", MailProvider.Proofpoint, ProviderFamily.Proofpoint,
            GatewayProvider.Proofpoint, 0.97),
        new("ppe-hosted.com", MailProvider.Proofpoint, ProviderFamily.Proofpoint,
            GatewayProvider.Proofpoint, 0.95),
        new("mimecast.com", MailProvider.Mimecast, ProviderFamily.Mimecast,
            GatewayProvider.Mimecast, 0.96),
        new("amazonses.com", MailProvider.AmazonSes, ProviderFamily.GenericSmtp,
            GatewayProvider.GenericSmtp, 0.92),
        new("messagingengine.com", MailProvider.Fastmail, ProviderFamily.GenericSmtp,
            GatewayProvider.GenericSmtp, 0.95),
        new("zoho.com", MailProvider.Zoho, ProviderFamily.GenericSmtp,
            GatewayProvider.GenericSmtp, 0.92)
    ];

    public MailProvider Detect(IReadOnlyList<MxRecord> records) => DetectWithConfidence(records).Provider;

    public ProviderDetectionResult DetectWithConfidence(IReadOnlyList<MxRecord> records)
    {
        var topology = CreateTopologyFingerprint(records);
        if (records.Count == 0)
            return new ProviderDetectionResult(MailProvider.Unknown, 0, TopologyFingerprint: topology);

        // Only the most-preferred published routes identify the active gateway.
        // A lower-priority Microsoft route behind a third-party MX must never cause
        // the validator to skip that published gateway.
        var minimumPreference = records.Min(record => record.Preference);
        foreach (var record in records.Where(record => record.Preference == minimumPreference))
        {
            var host = NormalizeHost(record.Host);
            foreach (var fingerprint in Fingerprints)
            {
                if (!MatchesDnsSuffix(host, fingerprint.Suffix)) continue;
                return new ProviderDetectionResult(
                    fingerprint.Provider,
                    fingerprint.Confidence,
                    fingerprint.Suffix,
                    fingerprint.Family,
                    fingerprint.Gateway,
                    MailProvider.Unknown,
                    host,
                    topology);
            }
        }

        var selectedHost = NormalizeHost(records.First(record => record.Preference == minimumPreference).Host);
        return new ProviderDetectionResult(
            MailProvider.GenericSmtp,
            0.55,
            "generic-mx",
            ProviderFamily.GenericSmtp,
            GatewayProvider.GenericSmtp,
            MailProvider.Unknown,
            selectedHost,
            topology);
    }

    private static bool MatchesDnsSuffix(string host, string suffix) =>
        host.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private static string? CreateTopologyFingerprint(IReadOnlyList<MxRecord> records) => records.Count == 0
        ? null
        : string.Join('|', records
            .Select(record => $"{record.Preference}:{NormalizeHost(record.Host)}")
            .OrderBy(value => value, StringComparer.Ordinal));
}
