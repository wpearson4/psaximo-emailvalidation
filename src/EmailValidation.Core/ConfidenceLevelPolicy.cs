namespace EmailValidation.Core;

/// <summary>
/// Maps canonical evidence to a small public confidence vocabulary without
/// presenting the legacy heuristic score as a delivery probability.
/// </summary>
public interface IConfidenceLevelPolicy
{
    ConfidenceLevel Evaluate(EmailValidationResult result);
}

/// <summary>
/// Qualitative confidence policy. Ambiguous, blocked, or absent mailbox evidence
/// cannot become High merely because the classifier is confident that it is inconclusive.
/// </summary>
public sealed class ConfidenceLevelPolicy : IConfidenceLevelPolicy
{
    public ConfidenceLevel Evaluate(EmailValidationResult result)
    {
        if (result.Status == EmailValidationStatus.Unknown)
            return ConfidenceLevel.Low;

        if (IsHighConfidenceConclusion(result))
            return ConfidenceLevel.High;

        return result.Confidence >= 0.65 && result.EvidenceQuality != EvidenceQuality.Unknown
            ? ConfidenceLevel.Medium
            : ConfidenceLevel.Low;
    }

    private static bool IsHighConfidenceConclusion(EmailValidationResult result)
    {
        if (result.Confidence < 0.85)
            return false;

        if (result.Status == EmailValidationStatus.CatchAll)
            return result.CatchAllClassification == CatchAllClassification.Confirmed;

        if (result.EvidenceQuality != EvidenceQuality.Conclusive)
            return false;

        return result.Status is EmailValidationStatus.Valid or EmailValidationStatus.Invalid;
    }
}
