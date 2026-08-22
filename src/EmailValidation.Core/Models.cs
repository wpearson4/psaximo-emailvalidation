namespace EmailValidation.Core;

// Append new values to preserve numeric compatibility with persisted intelligence.
public enum EmailValidationStatus { Valid, LikelyValid, Risky, Invalid, Unknown, LikelyInvalid, CatchAll }
public enum ConfidenceType { Heuristic, CalibratedProbability }
// Evidence quality describes how much direct validation evidence was obtained; it is independent of label confidence.
public enum EvidenceQuality { Unknown, Conclusive, Partial, Blocked, NotAttempted }
// Public CatchAll results retain the internal basis without reintroducing separate deliverability statuses.
public enum CatchAllClassification { None, Confirmed, Likely, GatewayAmbiguous, Historical }
// Distinguishes an observed remote result from work deliberately skipped by local scheduling policy.
public enum SmtpProbeDisposition { Completed, RemoteBlocked, LocalCooldown, SessionBudgetExhausted, NotAttempted }
public enum ProbeSenderHealthStatus { NotChecked, NotConfigured, InvalidSyntax, DomainNotFound, NoMailRouting, DnsUnavailable, Valid }
public enum ProbeSenderCandidateState { Candidate, Healthy, Active, CoolingDown, Invalid, Degraded, Retired }
public enum ProbeSenderOutcomeKind { MailFromAccepted, RecipientOutcome, SenderInvalid, SenderTemporaryFailure, ProviderRestriction, Inconclusive }
public enum ReasonCode
{
    InvalidSyntax, EmptyInput, MissingDomain, MissingLocalPart, DomainNotFound,
    InvalidDomain, NoMailExchanger, DnsTimeout, DnsFailure, SmtpDisabled,
    SmtpTimeout, MailboxRejected, MailboxAccepted, CatchAllDetected,
    CatchAllUnknown, DisposableDomain, RoleAccount, TemporarySmtpFailure,
    ProviderBlockedVerification, SmtpConnectionFailure, ImplicitMxFallback,
    GatewayAccepted, ProviderVerificationBlocked, Greylisted, RateLimited,
    CatchAllLikely, CatchAllUncertain, ProviderDetected, MailboxAcceptanceAmbiguous,
    HistoricalCatchAllBehavior, HistoricalVerificationBlocked, NullMailExchanger,
    MicrosoftRecipientRejected, NoMailRouting, UnroutableMailInfrastructure,
    MailboxFull, ToxicDomain, RoleBasedCatchAll, PossibleSpamTrap, KnownSpamTrap,
    AbuseRisk, SuppressionMatch, MxForward, FreeEmailProvider, TypoDetected,
    SuggestedDomainCorrection, TemporaryFailure, Timeout, Alias, AlternateAddress,
    SenderIdentityRejected, SenderDomainRejected, PolicyBlock, AuthenticationRequired,
    RelayDenied, ProbeSenderNotConfigured, ProbeSenderUnhealthy, MxResultsConflicting,
    LocalCooldown, RetryRecommended, CatchAllGatewayAmbiguous
}

public enum DnsStatus { Success, DomainNotFound, Timeout, Failure }
public enum SmtpMailboxStatus { NotAttempted, Accepted, Rejected, TemporaryFailure, ConnectionFailure, Timeout, Blocked, Unknown, MailboxFull }
public enum CatchAllStatus
{
    NotAttempted = 0,
    NotCatchAll = 1,
    LikelyCatchAll = 2,
    Unknown = 3,
    LikelyNotCatchAll = 4
}
public enum MailProvider
{
    Unknown = 0,
    Microsoft365 = 1,
    GoogleWorkspace = 2,
    Proofpoint = 3,
    Mimecast = 4,
    AmazonSes = 5,
    Fastmail = 6,
    Zoho = 7,
    GenericSmtp = 8,
    Yahoo = 9,
    MicrosoftConsumer = 10,
    AppleICloud = 11,
    Comcast = 12,
    Proton = 13
}

public enum ProviderFamily
{
    Unknown = 0,
    Microsoft365 = 1,
    GoogleWorkspace = 2,
    Proofpoint = 3,
    Mimecast = 4,
    GenericSmtp = 5,
    Yahoo = 6,
    MicrosoftConsumer = 7,
    AppleICloud = 8,
    Comcast = 9,
    Proton = 10,
    Fastmail = 11,
    Zoho = 12
}

public enum GatewayProvider
{
    Unknown = 0,
    MicrosoftExchangeOnlineProtection = 1,
    GoogleWorkspace = 2,
    Proofpoint = 3,
    Mimecast = 4,
    GenericSmtp = 5
}

public enum VerificationReliabilityLevel { Unknown = 0, Low = 1, Medium = 2, High = 3 }

public sealed record MxRecord(int Preference, string Host);

public sealed record DnsLookupResult(
    DnsStatus Status,
    bool DomainExists,
    IReadOnlyList<MxRecord> MxRecords,
    bool UsedAddressFallback,
    TimeSpan Duration,
    string? Error = null,
    bool ExplicitNullMx = false)
{
    public bool MxPresent => MxRecords.Count > 0;
    public bool ExplicitMxPresent => MxPresent && !UsedAddressFallback;
}

public sealed record SmtpProbeResult(
    SmtpMailboxStatus Status,
    int? ResponseCode,
    string? Response,
    TimeSpan ConnectionDuration,
    int Attempts = 1,
    SmtpEvidence? Evidence = null,
    SmtpSessionEvidence? SessionEvidence = null)
{
    /// <summary>All sessions used for transient retries or sender fallback, in attempt order.</summary>
    public IReadOnlyList<SmtpSessionEvidence> SessionHistory { get; init; } = [];
    /// <summary>Whether the result came from an SMTP session or from a local control path.</summary>
    public SmtpProbeDisposition Disposition { get; init; } = SmtpProbeDisposition.Completed;
    /// <summary>The earliest useful retry time when local policy deferred the probe.</summary>
    public DateTimeOffset? RetryAfter { get; init; }
    public bool ProbeAttempted => SessionEvidence is not null || Attempts > 0;
}

public sealed record ProbeSenderHealth(
    ProbeSenderHealthStatus Status,
    string? Sender,
    string? Domain,
    string Detail)
{
    public bool IsOperational => Status == ProbeSenderHealthStatus.Valid;
    public static ProbeSenderHealth NotChecked { get; } =
        new(ProbeSenderHealthStatus.NotChecked, null, null, "Live SMTP validation was not requested.");
}

public sealed record ProbeSenderCandidate(string Address, DateTimeOffset LoadedAt);

public sealed record ProbeSenderContext(
    IReadOnlySet<string> ExcludedSenders,
    string? RecipientDomain = null,
    string? PreferredSender = null)
{
    public static ProbeSenderContext Empty { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

public sealed record ProbeSenderSelection(string Sender, ProbeSenderCandidateState State);

public sealed record ProbeSenderOutcome(
    string Sender,
    ProbeSenderOutcomeKind Kind,
    SmtpProbeResult Result,
    string? RecipientDomain = null,
    ValidationFailureScope FailureScope = ValidationFailureScope.Unknown,
    bool SenderGloballyInvalid = false);

public enum ValidationFailureScope { Sender, Recipient, Domain, Provider, SourceIp, Connection, Unknown }

public sealed record ProbeSenderAffinity(
    string RecipientDomain,
    string Sender,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed class DomainPacingState
{
    public required string Domain { get; init; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? NextAllowedAttemptAt { get; set; }
    public int ActiveCount { get; set; }
    public int ConsecutiveTemporaryFailures { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }
}

public sealed record DomainBackoffDecision(DateTimeOffset NextAllowedAttemptAt, TimeSpan Cooldown);

public enum ProviderCircuitState { Closed, Open, HalfOpen }

public sealed record ProviderPolicy(
    string ProviderKey,
    int PerProviderConcurrency,
    int DelayMilliseconds,
    int PolicyBlockCooldownMinutes,
    int MaxRetries,
    int? PerDomainConcurrency = null);

public sealed record ProviderRuntimeState(
    string Provider,
    int ActiveCount,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAllowedAttemptAt,
    DateTimeOffset? CooldownUntil,
    ProviderCircuitState CircuitState,
    string? CooldownReason);

public sealed record ProviderThrottleAvailability(
    bool CanProbe,
    DateTimeOffset? RetryAfter = null,
    string? Reason = null)
{
    public static ProviderThrottleAvailability Available { get; } = new(true);
}

public sealed record ValidationWorkItem(long Sequence, string Email, EmailValidationRequest Request);

public sealed record ValidationWorkResult(
    long Sequence,
    EmailValidationResult Result,
    DateTimeOffset CompletedAt);

public sealed record DomainSchedulerSnapshot(
    long RowsScheduled,
    long RowsCompleted,
    int UniqueDomains,
    int ActiveDomains,
    int MaximumQueueDepth);

public sealed record SmtpSchedulingSnapshot(
    int TrackedDomains,
    int ActiveDomains,
    int CoolingDomains,
    int TrackedProviders,
    int CoolingProviders,
    long DomainCooldownEvents,
    long ProviderCooldownEvents,
    long PacingWaitMilliseconds,
    long HalfOpenAttempts = 0,
    long ProviderResumptions = 0,
    long ProviderConcurrencyWaits = 0,
    long ProviderPacingWaits = 0,
    long ProviderRetries = 0,
    long ProviderRetryExhaustions = 0);

public sealed record ProbeSenderAffinitySnapshot(
    int ActiveAffinities,
    long Created,
    long Retained,
    long Changed,
    long Removed,
    long CompatibilityRejections);

public sealed record ProbeSenderRuntimeStatistics(
    string Address,
    ProbeSenderCandidateState State,
    DateTimeOffset LoadedAt,
    DateTimeOffset? FirstUsedAt,
    DateTimeOffset? LastUsedAt,
    int ValidationCount,
    int ActiveValidationCount,
    int ActiveCompletedCount,
    int MailFromSuccessCount,
    int SenderFailureCount,
    int ConsecutiveSenderFailures,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset? ActiveSince);

public sealed record ProbeSenderPoolSnapshot(
    string Source,
    string Index,
    int QueryLimit,
    int CandidatesRetrieved,
    int Usable,
    int InvalidCandidates,
    string? ActiveSender,
    long PoolRefreshes,
    long SenderRotations,
    long ScheduledRotations,
    long FailureTriggeredRotations,
    long SenderCooldowns,
    long SenderRetirements,
    long PoolExhaustions,
    TimeSpan LastQueryDuration);

public sealed record ProbeSenderRotationDecision(bool ShouldRotate, string Reason)
{
    public static ProbeSenderRotationDecision Keep { get; } = new(false, string.Empty);
}

public sealed record CatchAllDetectionResult(
    CatchAllStatus Status,
    int Probes,
    int Accepted,
    int Rejected,
    int Ambiguous,
    string? Detail = null,
    double Confidence = 0)
{
    public bool RandomRecipientAccepted => Accepted > 0;
    public IReadOnlyList<SmtpProbeResult> ProbeResults { get; init; } = [];
}

public sealed record DomainValidationData(
    string Domain,
    DnsLookupResult Dns,
    bool DisposableDomain,
    MailProvider Provider,
    CatchAllStatus CatchAll,
    DateTimeOffset LastCheckedUtc,
    CatchAllDetectionResult? CatchAllEvidence = null);

public sealed record ValidationDiagnostics
{
    public bool DomainCacheHit { get; init; }
    public string? SelectedMx { get; init; }
    public long DnsDurationMs { get; init; }
    public long SmtpConnectionDurationMs { get; init; }
    public int SmtpAttempts { get; init; }
    public IReadOnlyList<string> MxHostsAttempted { get; init; } = [];
    public MxConsensus MxConsensus { get; init; }
    public string? ProbeSender { get; init; }
    public ProbeSenderHealthStatus SenderDomainHealth { get; init; }
    public int CatchAllProbes { get; init; }
    public int CatchAllAccepted { get; init; }
    public int CatchAllRejected { get; init; }
    public int CatchAllAmbiguous { get; init; }
    public string? CatchAllDetail { get; init; }
    public string? Detail { get; init; }
    public long IntelligenceLookupDurationMs { get; init; }
    public long MailInfrastructureDurationMs { get; init; }
    public bool ProbeAttempted { get; init; }
    public SmtpProbeDisposition ProbeDisposition { get; init; } = SmtpProbeDisposition.NotAttempted;
    public SmtpResponseCategory SmtpResponseCategory { get; init; } = SmtpResponseCategory.NotAttempted;
    public DateTimeOffset? RetryAfter { get; init; }
    public bool PersistentMailboxFound { get; init; }
    public bool PersistentDomainFound { get; init; }
    public bool PersistentMailboxFresh { get; init; }
    public string? PersistentIntelligenceDecision { get; init; }
}

public sealed record EmailValidationChecks
{
    public bool SyntaxValid { get; init; }
    public bool DomainExists { get; init; }
    public bool MxPresent { get; init; }
    public bool UsedImplicitMxFallback { get; init; }
    public bool DisposableDomain { get; init; }
    public bool RoleAccount { get; init; }
    public CatchAllStatus CatchAll { get; init; }
    public SmtpMailboxStatus Mailbox { get; init; }
}

public sealed record EmailValidationResult
{
    public required string Email { get; init; }
    public string? NormalizedEmail { get; init; }
    public EmailValidationStatus Status { get; init; }
    public double Confidence { get; init; }
    /// <summary>Confidence in the assigned classification, not a probability of delivery.</summary>
    public double ClassificationConfidence => Confidence;
    public ConfidenceType ConfidenceType { get; init; } = ConfidenceType.Heuristic;
    /// <summary>Populated only by a calibrated outcome model; heuristic classifications leave this null.</summary>
    public double? DeliverabilityProbability { get; init; }
    public double EvidenceConfidence => Confidence;
    public EvidenceQuality EvidenceQuality { get; init; } = EvidenceQuality.Unknown;
    public CatchAllClassification CatchAllClassification { get; init; } = CatchAllClassification.None;
    public bool ProbeAttempted { get; init; }
    public SmtpProbeDisposition ProbeDisposition { get; init; } = SmtpProbeDisposition.NotAttempted;
    public DateTimeOffset? RetryAfter { get; init; }
    public string? ConfidenceReason { get; init; }
    public required EmailValidationChecks Checks { get; init; }
    public MailProvider MailProvider { get; init; }
    public ProviderDetectionResult? Provider { get; init; }
    public IReadOnlyList<MxRecord> MxRecords { get; init; } = [];
    public bool UsedImplicitMxFallback { get; init; }
    public string? SelectedMx { get; init; }
    public IReadOnlyList<ReasonCode> ReasonCodes { get; init; } = [];
    public DomainIntelligence? DomainIntelligence { get; init; }
    public CatchAllDetectionResult? CatchAllEvidence { get; init; }
    public SmtpEvidence? SmtpEvidence { get; init; }
    public SmtpSessionEvidence? SmtpSessionEvidence { get; init; }
    public MxValidationEvidence? MxValidation { get; init; }
    public ProbeSenderHealth? ProbeSenderHealth { get; init; }
    public ProviderValidationResult? ProviderValidation { get; init; }
    public MailboxValidationDetails? Mailbox { get; init; }
    public CatchAllValidationDetails? CatchAll { get; init; }
    public HistoricalSignalSummary? HistoricalEvidence { get; init; }
    public IReadOnlyList<ConfidenceContribution> ConfidenceEvidence { get; init; } = [];
    public DetailedStatus DetailedStatus { get; init; } = DetailedStatus.Unknown;
    public IReadOnlyList<DetailedStatus> DetailedStatuses { get; init; } = [];
    public DetailedStatus SubStatus { get; init; } = DetailedStatus.Unknown;
    public IReadOnlyList<DetailedStatus> SubStatuses { get; init; } = [];
    public EmailAddressIntelligence? AddressIntelligence { get; init; }
    public ValidationRisk? Risk { get; init; }
    public EmailRiskResult? MailingRisk { get; init; }
    public SendRecommendation? Recommendation { get; init; }
    public IReadOnlyList<EvidenceProvenance> Evidence { get; init; } = [];
    public long DurationMs { get; init; }
    public ValidationDiagnostics? Diagnostics { get; init; }
    public ValidationResultMetadata? Metadata { get; init; }
}

public sealed record MailboxValidationDetails(
    SmtpMailboxStatus Result,
    double VerificationReliability,
    VerificationReliabilityLevel VerificationReliabilityLevel);

public sealed record CatchAllValidationDetails(CatchAllStatus Status, double Confidence);

public sealed record NormalizationResult(
    bool IsValid,
    string OriginalInput,
    string? NormalizedEmail,
    string? LocalPart,
    string? Domain,
    ReasonCode? FailureReason);

public sealed record ClassificationResult(
    EmailValidationStatus Status,
    double Confidence,
    IReadOnlyList<ReasonCode> ReasonCodes,
    IReadOnlyList<ConfidenceContribution>? ConfidenceEvidence = null);

public sealed record EmailValidationRequest(bool EnableSmtp = false, bool Verbose = false);

public sealed record SmtpThrottleContext(
    string Domain,
    string MxHost,
    MailProvider Provider = MailProvider.Unknown,
    string? OutboundIp = null,
    string? Tenant = null);
