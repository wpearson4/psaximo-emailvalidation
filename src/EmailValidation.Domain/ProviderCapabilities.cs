namespace EmailValidation.Core;

public enum ProviderCapabilityMode { Disabled, Shadow, Enforced }

public sealed record ProviderCapabilityProfile
{
    public bool ProbeMailbox { get; init; } = true;
    public bool ProbeControls { get; init; } = true;
    public bool ReuseConfirmedNonDiscrimination { get; init; } = true;
    public int NonDiscriminationRefreshMinutes { get; init; } = 60;
    public bool RequireTls { get; init; }
    public bool AllowSmtpUtf8 { get; init; } = true;
    public bool RetryVerificationBlocked { get; init; } = true;
    public bool RetryAmbiguousAcceptance { get; init; } = true;
    public int MinimumRetrySeconds { get; init; } = 5;
}

public sealed record ProviderCapabilityAssessment(
    string ProfileKey, string PolicyVersion, string PolicyHash, ProviderCapabilityMode Mode,
    bool WouldSkipMailbox, bool WouldSkipControls, bool Applied,
    DateTimeOffset? NextUsefulCheckAt, bool? ShadowStatusDisagrees = null)
{
    public bool? SmtpRetryPermitted { get; init; }
    public string? UnknownResponseFingerprint { get; init; }
    public bool WouldReuseNonDiscrimination { get; init; }
}
