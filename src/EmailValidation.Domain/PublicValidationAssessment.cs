namespace EmailValidation.Core;

/// <summary>Stable public interpretation, independent of retry finality and mailing risk.</summary>
public sealed record PublicValidationAssessment(
    string SummaryStatus,
    string ConfidenceType,
    double HeuristicEvidenceStrength,
    string EvidenceLevel,
    string RecipientBehavior,
    DateTimeOffset? MailboxEvidenceObservedAtUtc,
    DateTimeOffset? RoutingEvidenceObservedAtUtc,
    double? MailboxEvidenceAgeSeconds,
    double? RoutingEvidenceAgeSeconds,
    PublicProbabilityAssessment? Probability)
{
    public static string Summary(EmailValidationStatus status) => status switch
    {
        EmailValidationStatus.Valid => "Valid",
        EmailValidationStatus.Invalid => "Invalid",
        EmailValidationStatus.Unknown => "Inconclusive",
        _ => "Risky"
    };

    public static PublicValidationAssessment From(EmailValidationResult result, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var mailbox = result.MailboxEvidenceObservedAt;
        var routing = result.DomainIntelligence?.RoutingEvidence?.ObservedAt;
        // Preserve heuristic strength separately when enforcement changes classification confidence.
        return new(Summary(result.Status), result.ConfidenceType.ToString(), result.HeuristicEvidenceStrength,
            result.ConfidenceLevel.ToString().ToUpperInvariant(),
            (result.CatchAllEvidence?.EffectiveRecipientBehavior ??
             result.DomainIntelligence?.CatchAll.EffectiveRecipientBehavior ?? DomainRecipientBehavior.Unknown).ToString(),
            mailbox, routing, Age(mailbox, at), Age(routing, at),
            result.ProbabilityAssessment ?? PublicProbabilityAssessment.From(result.Prediction));
    }

    public PublicValidationAssessment At(DateTimeOffset now) => this with
    {
        MailboxEvidenceAgeSeconds = Age(MailboxEvidenceObservedAtUtc, now),
        RoutingEvidenceAgeSeconds = Age(RoutingEvidenceObservedAtUtc, now)
    };

    private static double? Age(DateTimeOffset? observation, DateTimeOffset now) =>
        observation is { } at ? Math.Max(0, (now - at).TotalSeconds) : null;
}

public sealed record PublicProbabilityAssessment(
    double Value, string Target, long OutcomeWindowSeconds, string ModelVersion,
    string CalibrationVersion, string OutcomeDefinitionVersion, string DecisionPolicyVersion,
    string TrainingDatasetId, string ArtifactChecksum, string RolloutMode, DateTimeOffset ScoredAtUtc)
{
    public static PublicProbabilityAssessment? From(EmailValidationPrediction? prediction)
    {
        if (prediction?.Model is not { } model ||
            model.RolloutMode is ModelRolloutMode.Disabled or ModelRolloutMode.Shadow ||
            prediction.Uncertainty.Disposition != PredictionDisposition.AcceptedPrediction ||
            string.IsNullOrWhiteSpace(model.CalibrationVersion) || model.CalibrationVersion == "uncalibrated") return null;
        var (value, target, window) = prediction switch
        {
            { MailboxExistenceProbability: { } p } => (p, "MailboxExistence", 0L),
            { TechnicalDeliveryProbability: { } p } => (p, "TechnicalDeliveryWithinWindow", 604800L),
            { HardBounceProbability: { } p } => (p, "HardBounceWithinWindow", 604800L),
            _ => (double.NaN, "", 0L)
        };
        if (!double.IsFinite(value) || value is < 0 or > 1) return null;
        return new(value, target, window, model.ModelVersion, model.CalibrationVersion,
            model.OutcomeDefinitionVersion, model.DecisionPolicyVersion, model.TrainingDatasetId,
            model.ArtifactChecksum, model.RolloutMode.ToString(), model.ScoredAtUtc);
    }
}
