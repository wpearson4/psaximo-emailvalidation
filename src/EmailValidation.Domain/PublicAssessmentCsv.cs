using System.Globalization;
namespace EmailValidation.Core;

public static class PublicAssessmentCsv
{
    public static readonly string[] Headers = ["Summary Status", "Score Type", "Evidence Level",
        "Mailbox Evidence Observed At", "Routing Evidence Observed At", "Mailbox Evidence Age Seconds",
        "Routing Evidence Age Seconds", "Probability", "Probability Target", "Outcome Window Seconds",
        "Model Version", "Calibration Version", "Outcome Definition Version", "Decision Policy Version",
        "Training Dataset ID", "Artifact Checksum", "Model Rollout Mode", "Scored At"];
    public static string[] Values(PublicValidationAssessment? a) => [
        a?.SummaryStatus ?? "", a?.ConfidenceType ?? "", a?.EvidenceLevel ?? "",
        a?.MailboxEvidenceObservedAtUtc?.ToString("O") ?? "", a?.RoutingEvidenceObservedAtUtc?.ToString("O") ?? "",
        Number(a?.MailboxEvidenceAgeSeconds), Number(a?.RoutingEvidenceAgeSeconds), Number(a?.Probability?.Value),
        a?.Probability?.Target ?? "", a?.Probability?.OutcomeWindowSeconds.ToString(CultureInfo.InvariantCulture) ?? "",
        a?.Probability?.ModelVersion ?? "", a?.Probability?.CalibrationVersion ?? "",
        a?.Probability?.OutcomeDefinitionVersion ?? "", a?.Probability?.DecisionPolicyVersion ?? "",
        a?.Probability?.TrainingDatasetId ?? "", a?.Probability?.ArtifactChecksum ?? "",
        a?.Probability?.RolloutMode ?? "", a?.Probability?.ScoredAtUtc.ToString("O") ?? ""];
    private static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
}
