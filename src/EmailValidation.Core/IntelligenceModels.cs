namespace EmailValidation.Core;

public sealed record ValidationPolicyVersions(
    string ValidationEngineVersion,
    string ClassificationPolicyVersion,
    string ConfidenceModelVersion,
    string ProviderStrategyVersion);

public sealed record ValidationResultMetadata(
    ValidationPolicyVersions Policy,
    DateTimeOffset ValidatedAt,
    bool Reused = false,
    DateTimeOffset? ReusedAt = null,
    string? MxTopologyFingerprint = null);

public sealed record MailboxIntelligence
{
    public required string NormalizedEmail { get; init; }
    public required EmailValidationStatus PreviousStatus { get; init; }
    public required SmtpMailboxStatus PreviousMailboxResult { get; init; }
    public required double PreviousConfidence { get; init; }
    public required ConfidenceType PreviousConfidenceType { get; init; }
    public required DateTimeOffset LastValidatedAt { get; init; }
    public DateTimeOffset? LastStrongPositiveEvidenceAt { get; init; }
    public DateTimeOffset? LastStrongNegativeEvidenceAt { get; init; }
    public required MailProvider ProviderAtValidation { get; init; }
    public required ValidationPolicyVersions Policy { get; init; }
    public IReadOnlyList<ReasonCode> ReasonCodes { get; init; } = [];
    public string? MxTopologyFingerprint { get; init; }
    public bool UsedLiveSmtp { get; init; }
    public required EmailValidationResult LastResult { get; init; }
}

public enum DeliveryOutcomeKind { Delivered, HardBounce, SoftBounce, Suppressed, Unknown }

public sealed record ValidationPredictionSnapshot(
    string NormalizedEmail,
    EmailValidationStatus PredictedStatus,
    double PredictedConfidence,
    ConfidenceType ConfidenceType,
    MailProvider Provider,
    CatchAllStatus CatchAllStatus,
    VerificationReliabilityLevel VerificationReliability,
    ValidationPolicyVersions Policy,
    DateTimeOffset ValidatedAt,
    IReadOnlyList<ReasonCode> ReasonCodes,
    string? DomainType = null,
    double EvidenceAgeHours = 0);

public static class ValidationPredictionSnapshots
{
    public static ValidationPredictionSnapshot FromResult(EmailValidationResult result)
    {
        if (result.NormalizedEmail is null || result.Metadata is null)
            throw new ArgumentException("A normalized, versioned validation result is required.", nameof(result));
        return new(
            result.NormalizedEmail,
            result.Status,
            result.Confidence,
            result.ConfidenceType,
            result.MailProvider,
            result.Checks.CatchAll,
            result.ProviderValidation?.VerificationReliabilityLevel ?? VerificationReliabilityLevel.Unknown,
            result.Metadata.Policy,
            result.Metadata.ValidatedAt,
            result.ReasonCodes.ToArray(),
            result.DomainIntelligence?.FreeEmailProvider == true ? "FreeEmail" : "CustomDomain",
            Math.Max(0, (DateTimeOffset.UtcNow - result.Metadata.ValidatedAt).TotalHours));
    }
}

public sealed record DeliveryOutcomeRecord(
    ValidationPredictionSnapshot Prediction,
    DeliveryOutcomeKind ActualOutcome,
    DateTimeOffset OutcomeObservedAt,
    string? Source = null);

public sealed record CalibrationQuery(
    MailProvider? Provider = null,
    EmailValidationStatus? Status = null,
    double? MinimumConfidence = null,
    double? MaximumConfidence = null,
    CatchAllStatus? CatchAllStatus = null,
    VerificationReliabilityLevel? VerificationReliability = null,
    ReasonCode? ReasonCode = null,
    string? DomainType = null,
    string? ClassificationPolicyVersion = null,
    string? ProviderStrategyVersion = null,
    double? MaximumEvidenceAgeHours = null);

public sealed record ConfidenceBandMetrics(
    double Minimum,
    double Maximum,
    int SampleCount,
    double DeliveryRate,
    double HardBounceRate,
    double CalibrationError);

public sealed record CalibrationMetrics(
    int SampleCount,
    double DeliveryRate,
    double HardBounceRate,
    double SoftBounceRate,
    double FalseValidRate,
    double FalseInvalidRate,
    double Precision,
    double Recall,
    double BrierScore,
    double CalibrationError);

public sealed record CalibrationResult(
    CalibrationQuery Query,
    CalibrationMetrics Metrics,
    IReadOnlyList<ConfidenceBandMetrics> ConfidenceBands,
    bool IsStatisticallyCalibrated,
    string ConfidenceStatement);

public enum MailingRiskLevel { Low, Medium, High, Unknown }
public enum MailingRiskReason
{
    KnownAbuse,
    KnownSuppression,
    ToxicDomain,
    SpamTrapIndicator,
    DisposableAddress,
    RoleAccount,
    SuspiciousDomain
}

public sealed record EmailRiskContext(
    string NormalizedEmail,
    EmailValidationStatus DeliverabilityStatus,
    double DeliverabilityConfidence,
    EmailValidationChecks Checks,
    DomainIntelligence? Domain,
    EmailAddressIntelligence? Address);

public sealed record RiskDataResult(
    string Source,
    MailingRiskLevel Level,
    IReadOnlyList<MailingRiskReason> Reasons,
    IReadOnlyList<EvidenceProvenance> Evidence);

public sealed record EmailRiskResult(
    EmailValidationStatus DeliverabilityStatus,
    double DeliverabilityConfidence,
    MailingRiskLevel MailingRisk,
    IReadOnlyList<MailingRiskReason> RiskReasons,
    IReadOnlyList<EvidenceProvenance> Evidence);

public sealed record SuppressionEntry(
    string NormalizedEmail,
    string Reason,
    string Source,
    DateTimeOffset SuppressedAt);

public sealed record ProviderQualitySnapshot(
    MailProvider Provider,
    long Total,
    double UnknownRate,
    double PolicyBlockRate,
    double RecipientRejectionRate,
    double CatchAllRate,
    double AverageVerificationReliability,
    double AverageValidationLatencyMs);

public sealed record ValidationQualitySnapshot(
    long TotalValidations,
    IReadOnlyDictionary<EmailValidationStatus, double> StatusRates,
    double BlockedVerificationRate,
    double CatchAllRate,
    double DisposableRate,
    double TypoRate,
    double KnownSuppressionRate,
    IReadOnlyList<ProviderQualitySnapshot> Providers);
