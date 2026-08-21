using EmailValidation.ConsoleApp;
using EmailValidation.Core;

namespace EmailValidation.Core.Tests;

public sealed class OutputCompatibilityTests
{
    [Fact]
    public void JsonOutput_PreservesExistingTopLevelProperties()
    {
        var json = ResultFormatter.Format([Result()], OutputFormat.Json, single: true);

        Assert.Contains("\"email\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\"", json, StringComparison.Ordinal);
        Assert.Contains("\"confidence\"", json, StringComparison.Ordinal);
        Assert.Contains("\"classificationConfidence\": 0.8", json, StringComparison.Ordinal);
        Assert.Contains("\"deliverabilityProbability\": null", json, StringComparison.Ordinal);
        Assert.Contains("\"checks\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mailProvider\"", json, StringComparison.Ordinal);
        Assert.Contains("\"provider\"", json, StringComparison.Ordinal);
        Assert.Contains("\"smtpEvidence\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mailbox\"", json, StringComparison.Ordinal);
        Assert.Contains("\"catchAll\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CsvOutput_PreservesExistingColumnsAndAddsEvidenceAtEnd()
    {
        var csv = ResultFormatter.Format([Result()], OutputFormat.Csv, single: false);
        var header = csv.Split('\n')[0];

        Assert.StartsWith("email,normalizedEmail,status,confidence,syntaxValid,domainExists,mxPresent", header, StringComparison.Ordinal);
        Assert.Contains("providerConfidence,catchAllConfidence,smtpCategory,enhancedStatusCode,detailedStatus", header, StringComparison.Ordinal);
        Assert.EndsWith("classificationConfidence,deliverabilityProbability", header, StringComparison.Ordinal);
    }

    [Fact]
    public void TextOutput_LabelsClassificationConfidenceAndUncalibratedProbability()
    {
        var text = ResultFormatter.Format([Result()], OutputFormat.Text, single: true);

        Assert.Contains("Classification Confidence: 80 %", text, StringComparison.Ordinal);
        Assert.Contains("Deliverability Probability: Not calibrated", text, StringComparison.Ordinal);
    }

    private static EmailValidationResult Result() => new()
    {
        Email = "user@example.com",
        NormalizedEmail = "user@example.com",
        Status = EmailValidationStatus.LikelyValid,
        Confidence = 0.80,
        Checks = new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true },
        MailProvider = MailProvider.GenericSmtp,
        Provider = new ProviderDetectionResult(MailProvider.GenericSmtp, 0.55),
        Mailbox = new MailboxValidationDetails(
            SmtpMailboxStatus.Accepted, 0.80, VerificationReliabilityLevel.High),
        CatchAll = new CatchAllValidationDetails(CatchAllStatus.NotCatchAll, 0.90),
        SmtpEvidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 250, "2.1.5", SmtpResponseCategory.Accepted,
            SmtpResponseTextClassification.Success, 10, MailProvider.GenericSmtp,
            "mx.example.com", 1, DateTimeOffset.UtcNow)
    };
}
