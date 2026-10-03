using EmailValidation.Core;

namespace EmailValidation.Core.Tests;

public sealed class ConfidenceLevelPolicyTests
{
    private readonly ConfidenceLevelPolicy _policy = new();

    [Fact]
    public void ConclusiveValidEvidence_IsHigh()
    {
        var result = Result(EmailValidationStatus.Valid, 0.91, EvidenceQuality.Conclusive);

        Assert.Equal(ConfidenceLevel.High, _policy.Evaluate(result));
    }

    [Fact]
    public void ConclusiveInvalidEvidence_IsHigh()
    {
        var result = Result(EmailValidationStatus.Invalid, 0.99, EvidenceQuality.Conclusive);

        Assert.Equal(ConfidenceLevel.High, _policy.Evaluate(result));
    }

    [Fact]
    public void HighLegacyScoreForUnknown_RemainsLow()
    {
        var result = Result(EmailValidationStatus.Unknown, 0.95, EvidenceQuality.Blocked);

        Assert.Equal(ConfidenceLevel.Low, _policy.Evaluate(result));
    }

    [Fact]
    public void LikelyConclusion_IsCappedAtMedium()
    {
        var result = Result(EmailValidationStatus.LikelyValid, 0.90, EvidenceQuality.Partial);

        Assert.Equal(ConfidenceLevel.Medium, _policy.Evaluate(result));
    }

    [Theory]
    [InlineData(CatchAllClassification.Confirmed, ConfidenceLevel.High)]
    [InlineData(CatchAllClassification.Likely, ConfidenceLevel.Medium)]
    [InlineData(CatchAllClassification.GatewayAmbiguous, ConfidenceLevel.Medium)]
    public void CatchAll_RequiresConfirmedEvidenceForHigh(
        CatchAllClassification classification,
        ConfidenceLevel expected)
    {
        var result = Result(EmailValidationStatus.CatchAll, 0.93, EvidenceQuality.Partial) with
        {
            CatchAllClassification = classification
        };

        Assert.Equal(expected, _policy.Evaluate(result));
    }

    private static EmailValidationResult Result(
        EmailValidationStatus status,
        double confidence,
        EvidenceQuality evidenceQuality) => new()
        {
            Email = "person@example.test",
            Status = status,
            Confidence = confidence,
            EvidenceQuality = evidenceQuality,
            Checks = new EmailValidationChecks()
        };
}
