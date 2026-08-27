using System.Net;

namespace EmailValidation.Core;

public enum ForwardConfirmedReverseDnsState
{
    NotEvaluated = 0,
    Valid = 1,
    MissingPtr = 2,
    UnexpectedPtr = 3,
    PtrMismatch = UnexpectedPtr,
    MissingForwardRecord = 4,
    ForwardAddressMismatch = 5,
    ForwardMismatch = ForwardAddressMismatch,
    DnsTemporaryFailure = 6,
    LookupFailed = DnsTemporaryFailure,
    LocalAddressNotBound = 7,
    WrongInterface = 8,
    MultiplePtrRecords = 9,
    MultipleForwardAddresses = 10,
    EhloMismatch = 11,
    InvalidHostname = 12,
    DnsResolverUnavailable = 13,
    InvalidConfiguration = 14
}

public enum OutboundIdentityDnsReadinessMode
{
    Disabled = 0,
    Observe = 1,
    Enforced = 2
}

public enum ForwardConfirmedReverseDnsValidationMode
{
    StrictOneToOne = 0,
    CompatibleContainsMatch = 1
}

public enum OutboundIdentityDnsQueryStatus
{
    Success = 0,
    NotFound = 1,
    NoData = 2,
    TemporaryFailure = 3,
    ResolverUnavailable = 4,
    MalformedResponse = 5
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
    NoEligibleIdentities = 5,
    NoDnsReadyIdentities = 6,
    InvalidIdentityConfiguration = 7
}

public sealed record OutboundIdentityDnsQueryResult(
    OutboundIdentityDnsQueryStatus Status,
    IReadOnlyList<string> HostNames,
    IReadOnlyList<IPAddress> Addresses,
    TimeSpan? TimeToLive = null);

public sealed record LocalOutboundIdentityBinding(
    string ExpectedInterfaceName,
    bool InterfaceExists,
    bool InterfaceOperational,
    bool AddressBound,
    string? ActualInterfaceName = null);

public sealed record OutboundIdentityDnsReadiness
{
    public required string IdentityId { get; init; }
    public required IPAddress Address { get; init; }
    public required string ExpectedHostName { get; init; }
    public required string EhloHostName { get; init; }
    public required ForwardConfirmedReverseDnsState State { get; init; }
    public required ForwardConfirmedReverseDnsState DnsState { get; init; }
    public required bool IsEligible { get; init; }
    public bool IsDegraded { get; init; }
    public IReadOnlyList<string> PtrHostNames { get; init; } = [];
    public IReadOnlyList<IPAddress> ForwardAddresses { get; init; } = [];
    public IReadOnlyList<ForwardConfirmedReverseDnsState> Warnings { get; init; } = [];
    public TimeSpan? PtrTtl { get; init; }
    public TimeSpan? ForwardTtl { get; init; }
    public DateTimeOffset EvaluatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset? LastKnownValidAtUtc { get; init; }
    public required string ValidationPolicyVersion { get; init; }
}

public sealed record OutboundIdentity
{
    public required string IdentityId { get; init; }
    public required IPAddress Address { get; init; }
    public required string InterfaceName { get; init; }
    public required string ExpectedPtrHostName { get; init; }
    public required string EhloHostName { get; init; }
    public bool Enabled { get; init; }
    public ForwardConfirmedReverseDnsState FcrDnsState { get; init; }
    public OutboundIdentityDnsReadiness? DnsReadiness { get; init; }
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
    public IReadOnlyDictionary<string, OutboundIdentityDnsReadiness> Readiness { get; init; } =
        new Dictionary<string, OutboundIdentityDnsReadiness>(StringComparer.OrdinalIgnoreCase);
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
