using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmailValidation.Core;

public sealed class ProviderCapabilityOptions
{
    public ProviderCapabilityMode Mode { get; set; } = ProviderCapabilityMode.Shadow;
    public string PolicyVersion { get; set; } = "provider-capabilities-v1";
    public Dictionary<string, ProviderCapabilityProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CanaryProviders { get; set; } = [];
    public string ApprovedPolicyHash { get; set; } = string.Empty;
    public string ApprovalReference { get; set; } = string.Empty;
}

/// <summary>Conservative action policy. A profile never converts acceptance or restrictions into mailbox truth.</summary>
public static class ProviderCapabilityPolicy
{
    private static readonly ProviderCapabilityProfile Baseline = new() { ReuseConfirmedNonDiscrimination = false, MinimumRetrySeconds = 0 };
    private static readonly ProviderCapabilityProfile Candidate = new();
    private static readonly Meter Meter = new("EmailValidation.ProviderCapabilities", "1.0.0");
    private static readonly Counter<long> PlanDifferences = Meter.CreateCounter<long>("provider_capability_plan_difference_total");
    private static readonly Counter<long> UnknownResponses = Meter.CreateCounter<long>("provider_capability_unknown_response_total");
    public static readonly IReadOnlySet<string> SupportedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Unknown", "Generic", "Microsoft365", "MicrosoftConsumer", "GoogleWorkspace", "Gmail", "Yahoo", "AOL",
      "Proofpoint", "Mimecast", "AmazonSes", "AppleICloud", "Comcast", "Proton", "Fastmail", "Zoho" };

    public static string Key(MailProvider provider, string? domain = null)
    {
        domain = domain?.Trim().TrimEnd('.').ToLowerInvariant();
        if (provider == MailProvider.GoogleWorkspace && domain is "gmail.com" or "googlemail.com") return "Gmail";
        if (provider == MailProvider.Yahoo && domain == "aol.com") return "AOL";
        return provider == MailProvider.GenericSmtp ? "Generic" : provider.ToString();
    }

    public static string Fingerprint(ProviderCapabilityOptions options)
    {
        var payload = JsonSerializer.Serialize(new
        {
            options.PolicyVersion,
            Profiles = options.Profiles.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new { Key = x.Key.ToUpperInvariant(), x.Value }),
            CanaryProviders = options.CanaryProviders.Select(x => x.ToUpperInvariant()).Order(StringComparer.Ordinal)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static bool Approved(ProviderCapabilityOptions options) =>
        !string.IsNullOrWhiteSpace(options.ApprovalReference) && options.CanaryProviders.Count > 0 &&
        string.Equals(options.ApprovedPolicyHash, Fingerprint(options), StringComparison.OrdinalIgnoreCase);

    public static ProviderCapabilityProfile Resolve(EmailValidationOptions options, MailProvider provider,
        string? domain = null, bool candidate = false)
    {
        var settings = options.ProviderCapabilities;
        var key = Key(provider, domain);
        if (settings.Mode == ProviderCapabilityMode.Disabled || !candidate &&
            (settings.Mode != ProviderCapabilityMode.Enforced || !Approved(settings) ||
             !settings.CanaryProviders.Contains(key, StringComparer.OrdinalIgnoreCase))) return Baseline;
        return settings.Profiles.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value ?? Candidate;
    }

    public static string StrategyVersion(EmailValidationOptions options) =>
        options.ProviderCapabilities.Mode == ProviderCapabilityMode.Enforced && Approved(options.ProviderCapabilities)
            ? $"{options.Policy.ProviderStrategyVersion}+cap-{Fingerprint(options.ProviderCapabilities)}"
            : options.Policy.ProviderStrategyVersion;

    public static ValidationPolicyVersions PolicyVersions(EmailValidationOptions options) =>
        options.Policy.ToVersions() with { ProviderStrategyVersion = StrategyVersion(options) };

    public static bool AllowsSmtpRetry(ProviderCapabilityProfile profile, EmailValidationResult result) =>
        result.ProviderCapabilities?.SmtpRetryPermitted is not false && profile.ProbeMailbox &&
        !result.ReasonCodes.Contains(ReasonCode.NonDiscriminationEvidenceReused) &&
        !result.ReasonCodes.Contains(ReasonCode.ProviderCapabilityRestricted) &&
        (profile.RetryVerificationBlocked || !result.ReasonCodes.Any(code => code is
            ReasonCode.ProviderBlockedVerification or ReasonCode.ProviderVerificationBlocked or ReasonCode.PolicyBlock)) &&
        (profile.RetryAmbiguousAcceptance || !result.ReasonCodes.Contains(ReasonCode.MailboxAcceptanceAmbiguous));

    public static void RecordPlan(string provider, ProviderCapabilityMode mode, string action) =>
        PlanDifferences.Add(1, new("provider", provider), new("mode", mode.ToString()), new("action", action));

    public static void RecordUnknownResponse(string provider) => UnknownResponses.Add(1, new KeyValuePair<string, object?>("provider", provider));
}
