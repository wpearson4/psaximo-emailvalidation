namespace EmailValidation.Core;

public enum DetailedStatus
{
    Unknown,
    MailboxAccepted,
    MailboxRejected,
    MailboxNotFound,
    MailboxFull,
    InvalidSyntax,
    DomainNotFound,
    NoMailRouting,
    UnroutableMailInfrastructure,
    CatchAll,
    RoleBased,
    RoleBasedCatchAll,
    Disposable,
    ToxicDomain,
    PossibleTrap,
    SpamTrapRisk,
    AbuseRisk,
    GlobalSuppression,
    MxForward,
    Greylisted,
    RateLimited,
    VerificationBlocked,
    TemporaryFailure,
    Timeout,
    TypoDetected,
    Alias,
    AlternateAddress,
    RecipientRejected,
    ProviderVerificationBlocked,
    SenderIdentityRejected,
    PolicyBlocked,
    LikelyCatchAll,
    DisposableAddress,
    RoleAccount,
    NullMx,
    NoMailExchanger,
    KnownSuppression,
    LocalCooldown,
    CatchAllConfirmed,
    CatchAllGatewayAmbiguous,
    CatchAllHistorical
}

public enum EvidenceSource
{
    Dns,
    Mx,
    Smtp,
    ProviderStrategy,
    HistoricalObservation,
    LocalIntelligence,
    ConfiguredIntelligenceProvider,
    Heuristic
}

public enum DisposableDomainStatus { Unknown, KnownDisposable, LikelyDisposable, NotDisposable }
public enum ToxicDomainStatus { Unknown, NoEvidence, LikelyToxic, KnownToxic }
public enum SpamTrapRiskStatus { Unknown, NoEvidence, PossibleSpamTrap, LikelySpamTrap, KnownSpamTrap }
public enum AbuseRiskStatus { Unknown, NoEvidence, KnownRisk }
public enum SuppressionStatus { Unknown, NotSuppressed, Suppressed }
public enum MxForwardStatus { Unknown, NoEvidence, LikelyForwarding, ConfirmedForwarding }
public enum MailInfrastructureStatus { Unknown, Routable, Unroutable }
public enum IdentityStatus { Unknown, NotDetected, Detected }
public enum BounceRisk { Unknown, Low, Moderate, High }
public enum RecommendationRisk { Unknown, Low, Moderate, High }

public sealed record EvidenceProvenance(
    string Signal,
    EvidenceSource Source,
    double Confidence,
    string Detail);

public sealed record TypoDetectionResult(
    bool TypoDetected,
    string? SuggestedDomain,
    string? SuggestedEmail,
    double Confidence,
    EvidenceSource EvidenceSource = EvidenceSource.Heuristic)
{
    public static TypoDetectionResult None { get; } = new(false, null, null, 0);
}

public sealed record DisposableDomainResult(
    DisposableDomainStatus Status,
    double Confidence,
    EvidenceSource? EvidenceSource = null)
{
    public static DisposableDomainResult Unknown { get; } = new(DisposableDomainStatus.Unknown, 0);
}

public sealed record ToxicDomainResult(
    ToxicDomainStatus Status,
    double Confidence,
    EvidenceSource? EvidenceSource = null)
{
    public static ToxicDomainResult Unknown { get; } = new(ToxicDomainStatus.Unknown, 0);
}

public sealed record SpamTrapRiskResult(
    SpamTrapRiskStatus Status,
    double Confidence,
    EvidenceSource? EvidenceSource = null)
{
    public static SpamTrapRiskResult Unknown { get; } = new(SpamTrapRiskStatus.Unknown, 0);
}

public sealed record AbuseRiskResult(
    AbuseRiskStatus Status,
    double Confidence,
    EvidenceSource? EvidenceSource = null)
{
    public static AbuseRiskResult Unknown { get; } = new(AbuseRiskStatus.Unknown, 0);
}

public sealed record SuppressionResult(
    SuppressionStatus Status,
    string? Reason,
    EvidenceSource? EvidenceSource = null)
{
    public static SuppressionResult Unknown { get; } = new(SuppressionStatus.Unknown, null);
}

public sealed record MxForwardResult(
    MxForwardStatus Status,
    string? ForwardingProvider,
    double Confidence,
    EvidenceSource? EvidenceSource = null)
{
    public static MxForwardResult Unknown { get; } = new(MxForwardStatus.Unknown, null, 0);
}

public sealed record DomainAgeResult(
    int? DomainAgeDays,
    double Confidence,
    EvidenceSource? EvidenceSource = null)
{
    public bool IsKnown => DomainAgeDays.HasValue;
    public static DomainAgeResult Unknown { get; } = new(null, 0);
}

public sealed record MailInfrastructureResult(
    MailInfrastructureStatus Status,
    IReadOnlyList<string> ResolvedMxHosts,
    IReadOnlyList<string> UnusableMxHosts,
    double Confidence,
    long DurationMs = 0,
    EvidenceSource EvidenceSource = EvidenceSource.Mx)
{
    public static MailInfrastructureResult Unknown { get; } = new(
        MailInfrastructureStatus.Unknown, [], [], 0);
}

public sealed record EmailIdentityResult(
    IdentityStatus AliasStatus,
    IdentityStatus AlternateAddressStatus,
    string? CanonicalAddress,
    double Confidence,
    EvidenceSource? EvidenceSource = null)
{
    public static EmailIdentityResult Unknown { get; } = new(
        IdentityStatus.Unknown, IdentityStatus.Unknown, null, 0);
}

public sealed record EmailAddressIntelligence
{
    public required string Email { get; init; }
    public TypoDetectionResult Typo { get; init; } = TypoDetectionResult.None;
    public SpamTrapRiskResult SpamTrapRisk { get; init; } = SpamTrapRiskResult.Unknown;
    public AbuseRiskResult AbuseRisk { get; init; } = AbuseRiskResult.Unknown;
    public SuppressionResult Suppression { get; init; } = SuppressionResult.Unknown;
    public EmailIdentityResult Identity { get; init; } = EmailIdentityResult.Unknown;
}

public sealed record ValidationRisk(
    BounceRisk BounceRisk,
    bool RoleBased,
    SpamTrapRiskStatus SpamTrapRisk,
    AbuseRiskStatus AbuseRisk);

public sealed record SendRecommendation(
    bool? Send,
    RecommendationRisk Risk,
    IReadOnlyList<string> Reasons);

public sealed record ResultEvaluation(
    DetailedStatus DetailedStatus,
    IReadOnlyList<DetailedStatus> DetailedStatuses,
    ValidationRisk Risk,
    SendRecommendation Recommendation,
    IReadOnlyList<EvidenceProvenance> Evidence,
    IReadOnlyList<ReasonCode> AdditionalReasonCodes);

public sealed record SupplementalDomainIntelligence(
    DisposableDomainResult Disposable,
    bool FreeEmailProvider,
    ToxicDomainResult ToxicDomain,
    MxForwardResult MxForward,
    DomainAgeResult DomainAge,
    MailInfrastructureResult MailInfrastructure,
    long LookupDurationMs);
