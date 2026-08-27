namespace EmailValidation.Core;

public enum PredictionTargetKind
{
    MailboxExistence,
    TechnicalDeliveryWithinWindow,
    HardBounceWithinWindow,
    VerificationReliability
}

public enum EmailDeliveryOutcome
{
    Delivered,
    HardBounce,
    SoftBounce,
    Complaint,
    Suppressed,
    RejectedBySenderPolicy,
    UnknownOutcome
}

public enum OutcomeConfidence { Untrusted, Low, Medium, High, Authoritative }
public enum OutcomeLabelState { Matured, Unresolved, RightCensored, Excluded }
public enum BinaryOutcomeLabel { Negative, Positive }

public sealed record OutcomeDefinition(
    PredictionTargetKind Target,
    string Version,
    TimeSpan MaturationPeriod,
    IReadOnlySet<EmailDeliveryOutcome> PositiveOutcomes,
    IReadOnlySet<EmailDeliveryOutcome> NegativeOutcomes,
    IReadOnlySet<EmailDeliveryOutcome> UnresolvedOutcomes,
    IReadOnlySet<EmailDeliveryOutcome> ExcludedOutcomes,
    string DuplicateResolutionPolicy,
    string ConflictingOutcomePolicy,
    string OutcomeSourcePrecedence,
    string ProviderNormalizationVersion);

public sealed record EmailDeliveryOutcomeObservation
{
    public required string OutcomeEventId { get; init; }
    public required string EmailCorrelationId { get; init; }
    public string? TenantId { get; init; }
    public string? ValidationId { get; init; }
    public required EmailDeliveryOutcome Outcome { get; init; }
    public required OutcomeConfidence Confidence { get; init; }
    public required string OutcomeSource { get; init; }
    public string? SourceEventId { get; init; }
    public MailProvider Provider { get; init; }
    public string? EnhancedStatusCode { get; init; }
    public string? NormalizedReason { get; init; }
    public required DateTimeOffset SendAttemptAtUtc { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required string NormalizationVersion { get; init; }
}

public sealed record SyntaxFeatureGroup(
    bool SyntaxValid,
    bool NormalizationSucceeded,
    bool RequiresSmtpUtf8,
    bool RoleAddress,
    bool Disposable,
    bool FreeProvider);

public sealed record DomainRoutingFeatureGroup(
    bool DomainExists,
    DnsStatus DnsStatus,
    bool MxPresent,
    bool ExplicitNullMx,
    int MxCount,
    bool UsedAddressFallback,
    MailProvider Provider,
    double ProviderEvidenceStrength,
    DnsSecurityState DnsSecurity,
    AuthenticationRecordState SpfState,
    AuthenticationRecordState DmarcState,
    CatchAllStatus CatchAllState,
    double CatchAllEvidenceStrength,
    string? MxTopologyFingerprint);

public sealed record SmtpFeatureGroup(
    SmtpProbeDisposition Disposition,
    SmtpCommand? StageReached,
    int? ReplyCode,
    string? EnhancedStatusCode,
    SmtpResponseCategory Category,
    SmtpNormalizedReason? NormalizedReason,
    bool RecipientStageReached,
    bool RecipientAccepted,
    bool SenderRejected,
    bool ProviderPolicyBlock,
    bool Greylisted,
    bool MailboxFull);

public sealed record HistoricalFeatureGroup(
    int ObservationCount,
    int ConclusiveCount,
    int ContradictoryCount,
    double VerificationReliability,
    VerificationReliabilityLevel VerificationReliabilityLevel,
    double TargetAcceptanceRate,
    double RecipientRejectionRate,
    double TemporaryFailureRate,
    double RateLimitRate,
    double GreylistingProbability);

public sealed record OperationalFeatureGroup(
    ValidationResultSource ResultSource,
    int AttemptNumber,
    EvidenceQuality EvidenceQuality,
    bool ProbeAttempted,
    SmtpReputationProtectionMode? ReputationMode,
    SmtpProbeBudgetDecision? ReputationDecision);

public sealed record EmailValidationFeatureSnapshot
{
    public required string SnapshotId { get; init; }
    public required string ValidationId { get; init; }
    public required string EmailCorrelationId { get; init; }
    public required string DomainCorrelationId { get; init; }
    public string? TenantId { get; init; }
    public required DateTimeOffset SnapshotAtUtc { get; init; }
    public required string FeatureSchemaVersion { get; init; }
    public required SyntaxFeatureGroup Syntax { get; init; }
    public required DomainRoutingFeatureGroup Domain { get; init; }
    public required SmtpFeatureGroup Smtp { get; init; }
    public required HistoricalFeatureGroup History { get; init; }
    public required OperationalFeatureGroup Operational { get; init; }
    public required double HeuristicEvidenceStrength { get; init; }
    public required EmailValidationStatus HeuristicStatus { get; init; }
}

public sealed record TrainingDatasetRequest(
    PredictionTargetKind Target,
    string OutcomeDefinitionVersion,
    string FeatureSchemaVersion,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset MaturationCutoffUtc,
    OutcomeConfidence MinimumOutcomeConfidence = OutcomeConfidence.High,
    string? TenantId = null,
    IReadOnlySet<MailProvider>? Providers = null);

public sealed record TrainingDatasetRow(
    string SnapshotId,
    string EmailCorrelationId,
    string DomainCorrelationId,
    DateTimeOffset SnapshotAtUtc,
    EmailValidationFeatureSnapshot Snapshot,
    BinaryOutcomeLabel Label,
    string OutcomeEventId,
    DateTimeOffset OutcomeObservedAtUtc,
    OutcomeConfidence OutcomeConfidence);

public sealed record TrainingDatasetManifest(
    string DatasetId,
    DateTimeOffset CreatedAtUtc,
    string FeatureSchemaVersion,
    string OutcomeDefinitionVersion,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset MaturationCutoffUtc,
    int TrainingRowCount,
    int PositiveCount,
    int NegativeCount,
    int ExcludedCount,
    int UnresolvedCount,
    int RightCensoredCount,
    IReadOnlyDictionary<MailProvider, int> ProviderDistribution,
    string DatasetHash,
    string SourceCheckpoint,
    string BuilderVersion);

public sealed record TrainingDataset(
    TrainingDatasetManifest Manifest,
    IReadOnlyList<TrainingDatasetRow> Rows);

public enum ModelRolloutMode { Disabled, Shadow, Advisory, Enforced }
public enum PredictionDisposition { AcceptedPrediction, Abstain, OutOfDistribution, InsufficientSupport }

public sealed record PredictionUncertainty(
    PredictionDisposition Disposition,
    string Reason,
    double? MissingFeatureFraction = null);

public sealed record PredictionModelMetadata(
    string ModelName,
    string ModelVersion,
    string FeatureSchemaVersion,
    string CalibrationVersion,
    string OutcomeDefinitionVersion,
    string DecisionPolicyVersion,
    DateTimeOffset TrainingDataCutoffUtc,
    string TrainingDatasetId,
    string ArtifactChecksum,
    DateTimeOffset ScoredAtUtc,
    ModelRolloutMode RolloutMode);

public sealed record RawModelPrediction(
    PredictionTargetKind Target,
    double RawScore,
    PredictionModelMetadata Model);

public sealed record CalibratedPrediction(
    PredictionTargetKind Target,
    double Probability,
    PredictionModelMetadata Model);

public sealed record ValidationDecision(
    EmailValidationStatus Status,
    string Reason,
    bool DeterministicOverride = false);

public sealed record EmailValidationPrediction
{
    public required double HeuristicEvidenceStrength { get; init; }
    public double? MailboxExistenceProbability { get; init; }
    public double? TechnicalDeliveryProbability { get; init; }
    public double? HardBounceProbability { get; init; }
    public double? VerificationReliability { get; init; }
    public required PredictionUncertainty Uncertainty { get; init; }
    public PredictionModelMetadata? Model { get; init; }
    public required ValidationDecision Decision { get; init; }
}

public sealed record ScoredEvaluationRow(
    double Probability,
    BinaryOutcomeLabel Label,
    MailProvider Provider,
    string DomainCorrelationId,
    bool Abstained = false);

public sealed record ProbabilityBandEvaluation(
    double Minimum,
    double Maximum,
    int Count,
    double MeanPredictedProbability,
    double ObservedPositiveRate,
    double StandardError);

public sealed record ProbabilityEvaluationMetrics(
    int Count,
    double BrierScore,
    double LogLoss,
    double ExpectedCalibrationError,
    double CalibrationIntercept,
    double CalibrationSlope,
    double FalseValidRate,
    double FalseInvalidRate,
    double Coverage,
    double AbstentionRate);

public sealed record ProbabilityEvaluationReport(
    ProbabilityEvaluationMetrics Overall,
    IReadOnlyList<ProbabilityBandEvaluation> ProbabilityBands,
    IReadOnlyDictionary<MailProvider, ProbabilityEvaluationMetrics> ProviderSegments,
    ProbabilityEvaluationMetrics UnseenDomain);
