using EmailValidation.Core;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class DisposableEmailDetector(IOptions<EmailValidationOptions> options) :
    IDisposableEmailDetector,
    IDisposableDomainIntelligenceProvider,
    IDisposableEmailDomainProvider
{
    private static readonly Meter Meter = new("EmailValidation.Disposable", "1.0.0");
    private static readonly Counter<long> Matches = Meter.CreateCounter<long>("disposable_domain_match");
    private readonly DisposableEmailOptions _options = options.Value.DisposableEmail;
    private readonly HashSet<string> _domains = new(
        options.Value.Intelligence.DisposableDomains,
        StringComparer.OrdinalIgnoreCase);

    public bool IsDisposable(string domain) => _options.Enabled && MatchesDomainOrParent(_domains, domain);

    public DisposableDomainResult Evaluate(string domain)
    {
        if (!IsDisposable(domain))
            return DisposableDomainResult.Unknown with
            {
                Source = "ConfiguredDomainDataset",
                DatasetVersion = _options.DatasetVersion,
                LastUpdatedUtc = DateTimeOffset.UtcNow
            };
        Matches.Add(1);
        return new DisposableDomainResult(
            DisposableDomainStatus.KnownDisposable,
            0.99,
            EvidenceSource.ConfiguredIntelligenceProvider,
            "ConfiguredDomainDataset",
            _options.DatasetVersion,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    public ValueTask<DisposableDomainResult> GetAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Evaluate(domain));
    }

    internal static bool MatchesDomainOrParent(IReadOnlySet<string> domains, string domain)
    {
        var normalized = domain.Trim().TrimEnd('.');
        return domains.Any(candidate =>
            normalized.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith('.' + candidate, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class RoleAccountDetector(IOptions<EmailValidationOptions> options) :
    IRoleAccountDetector,
    IRoleAddressDetector
{
    private static readonly Meter Meter = new("EmailValidation.RoleAddress", "1.0.0");
    private static readonly Counter<long> Matches = Meter.CreateCounter<long>("role_address_detected");
    private readonly HashSet<string> _roles = new(
        options.Value.Intelligence.RoleAccounts,
        StringComparer.OrdinalIgnoreCase);
    private readonly RiskIntelligenceOptions _options = options.Value.RiskIntelligence;

    public bool IsRoleAccount(string localPart)
    {
        var plus = localPart.IndexOf('+');
        var canonical = plus > 0 ? localPart[..plus] : localPart;
        return _roles.Contains(canonical);
    }

    public RoleAddressDetectionResult Detect(NormalizedEmailAddress email)
    {
        if (!_options.RoleDetectionEnabled) return RoleAddressDetectionResult.NotRole;
        var plus = email.LocalPart.IndexOf('+');
        var canonical = (plus > 0 ? email.LocalPart[..plus] : email.LocalPart).ToLowerInvariant();
        if (!_roles.Contains(canonical)) return RoleAddressDetectionResult.NotRole;
        Matches.Add(1);
        return new RoleAddressDetectionResult(
            true,
            canonical switch
            {
                "info" => RoleAddressType.Information,
                "sales" => RoleAddressType.Sales,
                "support" => RoleAddressType.Support,
                "admin" => RoleAddressType.Administration,
                "billing" => RoleAddressType.Billing,
                "contact" => RoleAddressType.Contact,
                "office" => RoleAddressType.Office,
                "help" => RoleAddressType.Help,
                "marketing" => RoleAddressType.Marketing,
                "abuse" => RoleAddressType.Abuse,
                "postmaster" => RoleAddressType.Postmaster,
                "webmaster" => RoleAddressType.Webmaster,
                "security" => RoleAddressType.Security,
                "hr" => RoleAddressType.HumanResources,
                "careers" => RoleAddressType.Careers,
                _ => RoleAddressType.Other
            },
            $"Local-part '{canonical}' matched the configured role-address rules.",
            _options.RoleRuleVersion);
    }
}

public sealed class MailProviderDetector : IMailProviderDetector
{
    private static readonly Dictionary<string, MailProvider> ProviderOwnedDomains =
        new Dictionary<string, MailProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["gmail.com"] = MailProvider.GoogleWorkspace,
            ["googlemail.com"] = MailProvider.GoogleWorkspace,
            ["outlook.com"] = MailProvider.MicrosoftConsumer,
            ["hotmail.com"] = MailProvider.MicrosoftConsumer,
            ["live.com"] = MailProvider.MicrosoftConsumer,
            ["msn.com"] = MailProvider.MicrosoftConsumer,
            ["yahoo.com"] = MailProvider.Yahoo,
            ["ymail.com"] = MailProvider.Yahoo,
            ["rocketmail.com"] = MailProvider.Yahoo,
            ["aol.com"] = MailProvider.Yahoo
        };
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
        new("olc.protection.outlook.com", MailProvider.MicrosoftConsumer, ProviderFamily.MicrosoftConsumer,
            GatewayProvider.MicrosoftExchangeOnlineProtection, 0.99),
        new("mx.microsoft", MailProvider.Microsoft365, ProviderFamily.Microsoft365,
            GatewayProvider.MicrosoftExchangeOnlineProtection, 0.99),
        new("google.com", MailProvider.GoogleWorkspace, ProviderFamily.GoogleWorkspace,
            GatewayProvider.GoogleWorkspace, 0.99),
        new("googlemail.com", MailProvider.GoogleWorkspace, ProviderFamily.GoogleWorkspace,
            GatewayProvider.GoogleWorkspace, 0.99),
        new("yahoodns.net", MailProvider.Yahoo, ProviderFamily.Yahoo,
            GatewayProvider.GenericSmtp, 0.99),
        new("mail.icloud.com", MailProvider.AppleICloud, ProviderFamily.AppleICloud,
            GatewayProvider.GenericSmtp, 0.99),
        new("mxge.comcast.net", MailProvider.Comcast, ProviderFamily.Comcast,
            GatewayProvider.GenericSmtp, 0.99),
        new("comcast.net", MailProvider.Comcast, ProviderFamily.Comcast,
            GatewayProvider.GenericSmtp, 0.97),
        new("protonmail.ch", MailProvider.Proton, ProviderFamily.Proton,
            GatewayProvider.GenericSmtp, 0.99),
        new("pphosted.com", MailProvider.Proofpoint, ProviderFamily.Proofpoint,
            GatewayProvider.Proofpoint, 0.97),
        new("ppe-hosted.com", MailProvider.Proofpoint, ProviderFamily.Proofpoint,
            GatewayProvider.Proofpoint, 0.95),
        new("mimecast.com", MailProvider.Mimecast, ProviderFamily.Mimecast,
            GatewayProvider.Mimecast, 0.96),
        new("amazonses.com", MailProvider.AmazonSes, ProviderFamily.GenericSmtp,
            GatewayProvider.GenericSmtp, 0.92),
        new("messagingengine.com", MailProvider.Fastmail, ProviderFamily.Fastmail,
            GatewayProvider.GenericSmtp, 0.95),
        new("zoho.com", MailProvider.Zoho, ProviderFamily.Zoho,
            GatewayProvider.GenericSmtp, 0.92)
    ];

    public MailProvider Detect(IReadOnlyList<MxRecord> records) => DetectWithConfidence(records).Provider;

    public ProviderDetectionResult DetectWithConfidence(
        string normalizedDomain,
        IReadOnlyList<MxRecord> records)
    {
        var mx = DetectWithConfidence(records);
        if (!ProviderOwnedDomains.TryGetValue(
                normalizedDomain.Trim().TrimEnd('.'), out var provider)) return mx;
        var (family, gateway) = provider switch
        {
            MailProvider.GoogleWorkspace => (ProviderFamily.GoogleWorkspace, GatewayProvider.GoogleWorkspace),
            MailProvider.MicrosoftConsumer => (ProviderFamily.MicrosoftConsumer,
                GatewayProvider.MicrosoftExchangeOnlineProtection),
            MailProvider.Yahoo => (ProviderFamily.Yahoo, GatewayProvider.GenericSmtp),
            _ => (ProviderFamily.Unknown, GatewayProvider.Unknown)
        };
        return mx with
        {
            Provider = provider,
            Confidence = 1,
            MatchedSignature = normalizedDomain,
            Family = family,
            GatewayProvider = gateway,
            Evidence = ["ExplicitProviderOwnedDomain"]
        };
    }

    public ProviderDetectionResult DetectWithConfidence(IReadOnlyList<MxRecord> records)
    {
        var topology = CreateTopologyFingerprint(records);
        if (records.Count == 0)
            return new ProviderDetectionResult(MailProvider.Unknown, 0, TopologyFingerprint: topology);

        // Only the most-preferred published routes identify the active gateway.
        // A lower-priority Microsoft route behind a third-party MX must never cause
        // the validator to skip that published gateway.
        var minimumPreference = records.Min(record => record.Preference);
        foreach (var record in records
                     .Where(record => record.Preference == minimumPreference)
                     .OrderBy(record => NormalizeHost(record.Host), StringComparer.Ordinal))
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

        var selectedHost = records
            .Where(record => record.Preference == minimumPreference)
            .Select(record => NormalizeHost(record.Host))
            .OrderBy(host => host, StringComparer.Ordinal)
            .First();
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

public sealed class SmtpBannerProviderDetector : ISmtpProviderDetector
{
    private sealed record Signature(string Token, MailProvider Provider, double Confidence);

    private static readonly Signature[] Signatures =
    [
        new("outlook.com", MailProvider.Microsoft365, 0.90),
        new("microsoft", MailProvider.Microsoft365, 0.82),
        new("google.com", MailProvider.GoogleWorkspace, 0.90),
        new("google", MailProvider.GoogleWorkspace, 0.80),
        new("yahoodns.net", MailProvider.Yahoo, 0.88),
        new("proofpoint", MailProvider.Proofpoint, 0.88),
        new("mimecast", MailProvider.Mimecast, 0.88),
        new("protonmail", MailProvider.Proton, 0.88),
        new("zoho", MailProvider.Zoho, 0.84),
        new("messagingengine.com", MailProvider.Fastmail, 0.88)
    ];

    public ProviderDetectionResult Detect(SmtpSessionEvidence evidence)
    {
        var source = string.Join(' ', new[] { evidence.ServerBanner, evidence.EhloHost }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        foreach (var signature in Signatures)
        {
            if (!source.Contains(signature.Token, StringComparison.OrdinalIgnoreCase)) continue;
            return new ProviderDetectionResult(
                signature.Provider,
                signature.Confidence,
                MatchedSignature: signature.Token,
                Evidence: ["SmtpGreetingOrEhlo"],
                DetectedAtUtc: DateTimeOffset.UtcNow,
                DetectionVersion: "smtp-banner-1.0.0",
                SmtpObservedProvider: signature.Provider,
                SmtpEvidenceConfidence: signature.Confidence);
        }
        return new ProviderDetectionResult(
            MailProvider.Unknown,
            0,
            Evidence: source.Length == 0 ? [] : ["UnrecognizedSmtpGreetingOrEhlo"],
            DetectedAtUtc: DateTimeOffset.UtcNow,
            DetectionVersion: "smtp-banner-1.0.0");
    }
}

public sealed class ConfiguredSpamTrapRiskProvider(ISpamTrapRiskDetector detector) : ISpamTrapRiskProvider
{
    private static readonly Meter Meter = new("EmailValidation.SpamTrap", "1.0.0");
    private static readonly Counter<long> KnownMatches = Meter.CreateCounter<long>("spam_trap_known_match");

    public async Task<SpamTrapRiskAssessment> EvaluateAsync(
        EmailRiskContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await detector.EvaluateAsync(context.NormalizedEmail, cancellationToken).ConfigureAwait(false);
        var assessment = result.Status switch
        {
            SpamTrapRiskStatus.KnownSpamTrap when result.EvidenceSource is not EvidenceSource.Heuristic =>
                new(SpamTrapRiskLevel.Known, SpamTrapEvidenceKind.TrustedDatasetMatch,
                    result.Confidence, result.EvidenceSource?.ToString()),
            SpamTrapRiskStatus.LikelySpamTrap => new(SpamTrapRiskLevel.High,
                result.EvidenceSource == EvidenceSource.Heuristic
                    ? SpamTrapEvidenceKind.HeuristicOnly
                    : SpamTrapEvidenceKind.DomainRiskPattern,
                result.Confidence, result.EvidenceSource?.ToString()),
            SpamTrapRiskStatus.PossibleSpamTrap => new(SpamTrapRiskLevel.Elevated,
                SpamTrapEvidenceKind.HeuristicOnly, result.Confidence, result.EvidenceSource?.ToString()),
            _ => SpamTrapRiskAssessment.None
        };
        if (assessment.Level == SpamTrapRiskLevel.Known) KnownMatches.Add(1);
        return assessment;
    }
}
