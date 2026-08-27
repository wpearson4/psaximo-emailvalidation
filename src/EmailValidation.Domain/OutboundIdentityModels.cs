using System.Net;

namespace EmailValidation.Core;

public enum ForwardConfirmedReverseDnsState
{
    NotEvaluated = 0,
    Valid = 1,
    MissingPtr = 2,
    PtrMismatch = 3,
    MissingForwardRecord = 4,
    ForwardMismatch = 5,
    LookupFailed = 6
}

public enum OutboundIdentityHealthState
{
    Healthy = 0,
    Degraded = 1,
    Cooldown = 2,
    Quarantined = 3,
    Disabled = 4,
    Misconfigured = 5
}

public enum OutboundIdentitySelectionReason
{
    Selected = 0,
    FeatureDisabled = 1,
    ProviderGroupNotConfigured = 2,
    NoConfiguredIdentities = 3,
    NoLocallyBoundIdentities = 4,
    NoEligibleIdentities = 5
}

public sealed record OutboundIdentity
{
    public required string IdentityId { get; init; }
    public required IPAddress Address { get; init; }
    public required string InterfaceName { get; init; }
    public required string EhloHostName { get; init; }
    public bool Enabled { get; init; }
    public ForwardConfirmedReverseDnsState FcrDnsState { get; init; }
}

public sealed record OutboundIdentityHealth(
    string IdentityId,
    MailProvider Provider,
    OutboundIdentityHealthState State,
    DateTimeOffset? CooldownUntil = null,
    int AttributableFailureCount = 0,
    string? Reason = null)
{
    public bool IsEligible(DateTimeOffset now) =>
        State is OutboundIdentityHealthState.Healthy or OutboundIdentityHealthState.Degraded ||
        State is OutboundIdentityHealthState.Cooldown or OutboundIdentityHealthState.Quarantined &&
        CooldownUntil <= now;
}

public sealed record OutboundIdentitySelectionRequest(
    string NormalizedRecipientDomain,
    MailProvider Provider);

public sealed record OutboundIdentitySelectionResult(
    OutboundIdentity? Identity,
    OutboundIdentitySelectionReason Reason,
    string ProviderGroup,
    string AlgorithmVersion,
    IReadOnlyList<string> RejectedIdentityIds)
{
    public bool Selected => Identity is not null;
}

public sealed record OutboundIdentityOutcome(
    string IdentityId,
    MailProvider Provider,
    SmtpResponseCategory Category,
    SmtpCooldownScope CooldownScope,
    SmtpHealthImpact HealthImpact,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? RetryAfter = null,
    string? Reason = null,
    bool Global = false);
