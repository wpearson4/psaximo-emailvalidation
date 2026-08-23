namespace EmailValidation.Core;

public enum SmtpCommand { Connect, Greeting, Ehlo, Helo, MailFrom, RcptTo, Rset, Quit }

public enum SmtpResponseCategory
{
    NotAttempted,
    Accepted,
    RecipientRejected,
    TemporaryFailure,
    Greylisted,
    RateLimited,
    VerificationBlocked,
    GatewayAccepted,
    MailboxUnknown,
    ConnectionRejected,
    Timeout,
    ProtocolFailure,
    Unknown,
    MailboxFull,
    LocalCooldown,
    SmtpUtf8Unsupported
}

public enum SmtpResponseTextClassification
{
    None,
    Success,
    RecipientDoesNotExist,
    MailboxUnavailable,
    PolicyRejection,
    RelayDenied,
    AntiAbuse,
    Greylisting,
    RateLimit,
    TemporaryCondition,
    VerificationUnavailable,
    Unknown,
    MailboxFull
}

public enum AcceptanceStrength { None, Low, Medium, High }
public enum ValidationObservationType { Domain, CatchAllProbe, MailboxProbe }

public sealed record ProviderDetectionResult(
    MailProvider Provider,
    double Confidence,
    string? MatchedSignature = null,
    ProviderFamily Family = ProviderFamily.Unknown,
    GatewayProvider GatewayProvider = GatewayProvider.Unknown,
    MailProvider MailboxProvider = MailProvider.Unknown,
    string? MxHost = null,
    string? TopologyFingerprint = null,
    IReadOnlyList<string>? Evidence = null,
    DateTimeOffset? DetectedAtUtc = null,
    string DetectionVersion = "1.0.0",
    MailProvider SmtpObservedProvider = MailProvider.Unknown,
    double SmtpEvidenceConfidence = 0);

public sealed record SmtpEvidence(
    SmtpCommand Command,
    int? ResponseCode,
    string? EnhancedStatusCode,
    SmtpResponseCategory Category,
    SmtpResponseTextClassification TextClassification,
    long ElapsedMilliseconds,
    MailProvider Provider,
    string MxHost,
    int Attempt,
    DateTimeOffset Timestamp,
    string? SanitizedResponse = null);

/// <summary>
/// Evidence for one command in an SMTP conversation. A response is never
/// interpreted independently of the command that produced it.
/// </summary>
public sealed record SmtpStageResult(
    SmtpCommand Stage,
    int? ResponseCode,
    string? EnhancedStatusCode,
    SmtpResponseCategory Category,
    SmtpResponseTextClassification TextClassification,
    TimeSpan Duration,
    string? SanitizedResponse = null);

/// <summary>
/// Complete command-stage provenance for a single connection to one MX host.
/// </summary>
public sealed record SmtpSessionEvidence(
    SmtpCommand? FailedStage,
    IReadOnlyList<SmtpStageResult> Stages,
    string MxHost,
    TimeSpan Duration,
    string ProbeSender,
    string? ServerBanner = null,
    string? EhloHost = null,
    bool TlsAdvertised = false,
    bool TlsUsed = false,
    bool SmtpUtf8Advertised = false,
    bool SmtpUtf8Required = false)
{
    public SmtpStageResult? MailFrom => Stages.LastOrDefault(stage => stage.Stage == SmtpCommand.MailFrom);
    public SmtpStageResult? RcptTo => Stages.LastOrDefault(stage => stage.Stage == SmtpCommand.RcptTo);
    public bool MailFromSucceeded => MailFrom?.ResponseCode is >= 200 and < 300;
    public bool RecipientStageReached => MailFromSucceeded && RcptTo is not null;
    public bool HasStrongRecipientRejection => RecipientStageReached &&
        RcptTo!.Category == SmtpResponseCategory.RecipientRejected;
}

public enum MxConsensus
{
    Unknown,
    ConclusivePositive,
    ConclusiveNegative,
    ConsistentAmbiguous,
    Conflicting
}

public sealed record MxValidationEvidence(
    IReadOnlyList<SmtpProbeResult> Attempts,
    IReadOnlyList<string> HostsAttempted,
    MxConsensus Consensus);

public sealed record DomainIntelligence
{
    public required string Domain { get; init; }
    public bool DomainExists { get; init; }
    public required DnsLookupResult Dns { get; init; }
    public IReadOnlyList<MxRecord> MxRecords => Dns.MxRecords;
    public MailRoutingIntelligence? MailRouting { get; init; }
    public required ProviderDetectionResult Provider { get; init; }
    public DnsSecurityIntelligence DnsSecurity { get; init; } = DnsSecurityIntelligence.Unknown;
    public EmailAuthenticationIntelligence Authentication { get; init; } = EmailAuthenticationIntelligence.Unknown;
    public bool Disposable { get; init; }
    public DisposableDomainResult DisposableIntelligence { get; init; } = DisposableDomainResult.Unknown;
    public bool FreeEmailProvider { get; init; }
    public ToxicDomainResult ToxicDomain { get; init; } = ToxicDomainResult.Unknown;
    public MxForwardResult MxForward { get; init; } = MxForwardResult.Unknown;
    public DomainAgeResult DomainAge { get; init; } = DomainAgeResult.Unknown;
    public MailInfrastructureResult MailInfrastructure { get; init; } = MailInfrastructureResult.Unknown;
    public CatchAllDetectionResult CatchAll { get; init; } =
        new(CatchAllStatus.NotAttempted, 0, 0, 0, 0);
    public DomainBehaviorProfile? Behavior { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset? EvidenceExpiresAt { get; init; }
    public string StrategyVersion { get; init; } = "1.0.0";
    public string? MxTopologyFingerprint { get; init; }
    public string? ProviderFingerprint { get; init; }
    public string? AuthenticationFingerprint { get; init; }
    public string? CatchAllFingerprint { get; init; }
    public DateTimeOffset FirstObservedUtc { get; init; }
    public DateTimeOffset LastObservedUtc { get; init; }
    public DateTimeOffset? LastChangedUtc { get; init; }
    public int ChangeCount { get; init; }
    public string IntelligencePolicyVersion { get; init; } = "1.0.0";
}

public sealed record MailRoutingIntelligence(
    DnsStatus Status,
    bool DomainExists,
    IReadOnlyList<MxRecord> Routes,
    bool UsedAddressFallback,
    bool ExplicitNullMx,
    IReadOnlyList<string> Ipv4Addresses,
    IReadOnlyList<string> Ipv6Addresses,
    TimeSpan? TimeToLive,
    DateTimeOffset ObservedAtUtc,
    string? Error = null,
    TimeSpan LookupDuration = default)
{
    public bool HasUsableRoute => Routes.Count > 0 && !ExplicitNullMx;
}

public enum DnsSecurityState { Unknown, NotPresent, Secure, Bogus, Indeterminate }
public enum IntelligenceAvailability { Available, NotAvailable, Degraded, Failed }

public sealed record DnsSecurityIntelligence(
    DnsSecurityState State,
    IntelligenceAvailability Availability,
    DateTimeOffset ObservedAtUtc,
    string? Detail = null)
{
    public static DnsSecurityIntelligence Unknown { get; } = new(
        DnsSecurityState.Unknown, IntelligenceAvailability.NotAvailable, default);
}

public enum AuthenticationRecordState { Unknown, NotPresent, Valid, Invalid, LookupFailed }
public enum DkimObservationState { Unknown, Observed, NotEvaluated }
public enum DmarcPolicy { Unknown, None, Quarantine, Reject }

public sealed record SpfIntelligence(
    AuthenticationRecordState State,
    string? AllMechanism = null,
    string? Record = null,
    string? Detail = null)
{
    public static SpfIntelligence Unknown { get; } = new(AuthenticationRecordState.Unknown);
}

public sealed record DmarcIntelligence(
    AuthenticationRecordState State,
    DmarcPolicy Policy = DmarcPolicy.Unknown,
    DmarcPolicy? SubdomainPolicy = null,
    int? Percentage = null,
    string? Record = null,
    string? Detail = null)
{
    public static DmarcIntelligence Unknown { get; } = new(AuthenticationRecordState.Unknown);
}

public sealed record DkimIntelligence(
    DkimObservationState State,
    IReadOnlyList<string> ObservedSelectors,
    string? Detail = null)
{
    public static DkimIntelligence NotEvaluated { get; } = new(
        DkimObservationState.NotEvaluated, [], "DKIM selectors are not exhaustively discoverable.");
}

public sealed record EmailAuthenticationIntelligence(
    SpfIntelligence Spf,
    DmarcIntelligence Dmarc,
    DkimIntelligence Dkim,
    IntelligenceAvailability Availability,
    DateTimeOffset ObservedAtUtc)
{
    public static EmailAuthenticationIntelligence Unknown { get; } = new(
        SpfIntelligence.Unknown,
        DmarcIntelligence.Unknown,
        DkimIntelligence.NotEvaluated,
        IntelligenceAvailability.NotAvailable,
        default);
}

public sealed record DomainBehaviorProfile(
    string Domain,
    GatewayProvider GatewayProvider,
    int ObservationCount,
    double TargetAcceptanceRate,
    double RandomAcceptanceRate,
    double RecipientRejectionRate,
    double TemporaryFailureRate,
    double RateLimitRate,
    double GatewayAcceptanceRate,
    double VerificationReliability,
    VerificationReliabilityLevel VerificationReliabilityLevel,
    string? TopologyFingerprint,
    double GreylistingProbability = 0);

public sealed record MailboxEvidence(
    string Domain,
    string MxHost,
    SmtpProbeResult Probe,
    ProviderValidationResult ProviderEvaluation);

public sealed record ProviderValidationContext(
    DomainIntelligence Domain,
    SmtpProbeResult MailboxProbe,
    HistoricalSignalSummary History);

public sealed record ProviderValidationResult(
    MailProvider Provider,
    double ProviderConfidence,
    SmtpResponseCategory EffectiveCategory,
    AcceptanceStrength AcceptanceStrength,
    IReadOnlyList<ReasonCode> ReasonCodes,
    string Explanation,
    GatewayProvider GatewayProvider = GatewayProvider.Unknown,
    MailProvider MailboxProvider = MailProvider.Unknown,
    double VerificationReliability = 0,
    VerificationReliabilityLevel VerificationReliabilityLevel = VerificationReliabilityLevel.Unknown);

public sealed record ValidationObservation(
    string Domain,
    ValidationObservationType Type,
    MailProvider Provider,
    string? MxHost,
    CatchAllStatus CatchAllStatus,
    double CatchAllConfidence,
    SmtpResponseCategory ResponseCategory,
    DateTimeOffset ObservedAt,
    long DurationMilliseconds,
    int RandomRecipientAcceptedCount = 0,
    int RandomRecipientProbeCount = 0,
    int RandomRecipientRejectedCount = 0,
    GatewayProvider GatewayProvider = GatewayProvider.Unknown,
    string? TopologyFingerprint = null);

public sealed record HistoricalSignalSummary(
    int ObservationCount,
    int LikelyCatchAllCount,
    int VerificationBlockedCount,
    int GatewayAcceptedCount,
    int TemporaryFailureCount,
    int RateLimitedCount,
    int RandomRecipientAcceptedCount = 0,
    int TargetAcceptedCount = 0,
    int TargetRejectedCount = 0,
    int RandomRecipientProbeCount = 0,
    int RandomRecipientRejectedCount = 0,
    double TargetAcceptanceRate = 0,
    double RandomAcceptanceRate = 0,
    double RecipientRejectionRate = 0,
    double TemporaryFailureRate = 0,
    double RateLimitRate = 0,
    double GatewayAcceptanceRate = 0,
    double VerificationReliability = 0,
    VerificationReliabilityLevel VerificationReliabilityLevel = VerificationReliabilityLevel.Unknown,
    int GreylistedCount = 0,
    double GreylistingProbability = 0)
{
    public static HistoricalSignalSummary Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public sealed record ConfidenceContribution(string Evidence, double Weight, string Explanation);

public sealed record EmailClassificationEvidence(
    bool SyntaxValid,
    DnsStatus DnsStatus,
    DomainIntelligence? Domain,
    bool RoleAccount,
    MailboxEvidence? Mailbox,
    HistoricalSignalSummary History)
{
    public EmailAddressIntelligence? AddressIntelligence { get; init; }
}

public sealed record DeliveryOutcome(
    string Domain,
    bool Delivered,
    DateTimeOffset ObservedAt,
    string? Source = null);
