using EmailValidation.Core;

namespace EmailValidation.Core.Tests;

public sealed class EvidenceClassificationTests
{
    private readonly EmailClassificationEngine _classifier = new();

    [Fact]
    public void StrongGenericMailboxEvidence_IsValid()
    {
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.Accepted,
            AcceptanceStrength.High,
            CatchAllStatus.NotCatchAll,
            catchAllConfidence: 0.90));

        Assert.Equal(EmailValidationStatus.Valid, result.Status);
        Assert.InRange(result.Confidence, 0.85, 1.0);
        Assert.NotEmpty(result.ConfidenceEvidence!);
    }

    [Fact]
    public void CatchAllAcceptance_IsRiskyNotDefinitivelyValid()
    {
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.GatewayAccepted,
            AcceptanceStrength.Low,
            CatchAllStatus.LikelyCatchAll,
            catchAllConfidence: 0.90));

        Assert.Equal(EmailValidationStatus.Risky, result.Status);
        Assert.Contains(ReasonCode.CatchAllLikely, result.ReasonCodes);
    }

    [Fact]
    public void GatewayAcceptanceWithoutCatchAllProof_IsLikelyValid()
    {
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.GatewayAccepted,
            AcceptanceStrength.Low,
            CatchAllStatus.Unknown,
            provider: MailProvider.Microsoft365));

        Assert.Equal(EmailValidationStatus.LikelyValid, result.Status);
        Assert.NotEqual(EmailValidationStatus.Valid, result.Status);
    }

    [Fact]
    public void ExplicitRecipientRejection_IsInvalid()
    {
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.RecipientRejected,
            AcceptanceStrength.None,
            CatchAllStatus.NotCatchAll));

        Assert.Equal(EmailValidationStatus.Invalid, result.Status);
        Assert.Contains(ReasonCode.MailboxRejected, result.ReasonCodes);
    }

    [Theory]
    [InlineData(SmtpResponseCategory.VerificationBlocked, ReasonCode.ProviderVerificationBlocked)]
    [InlineData(SmtpResponseCategory.Greylisted, ReasonCode.Greylisted)]
    [InlineData(SmtpResponseCategory.RateLimited, ReasonCode.RateLimited)]
    [InlineData(SmtpResponseCategory.TemporaryFailure, ReasonCode.TemporarySmtpFailure)]
    public void AmbiguousOrTemporaryEvidence_IsUnknown(SmtpResponseCategory category, ReasonCode reason)
    {
        var result = _classifier.Classify(Evidence(category, AcceptanceStrength.None, CatchAllStatus.Unknown));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(reason, result.ReasonCodes);
    }

    [Fact]
    public void RepeatedHistoricalCatchAllEvidence_ProducesRisk()
    {
        var history = new HistoricalSignalSummary(3, 2, 0, 1, 0, 0);
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.Accepted,
            AcceptanceStrength.High,
            CatchAllStatus.Unknown,
            history: history));

        Assert.Equal(EmailValidationStatus.Risky, result.Status);
        Assert.Contains(ReasonCode.HistoricalCatchAllBehavior, result.ReasonCodes);
    }

    [Fact]
    public void RepeatedHistoricalRandomAcceptance_StrengthensGenericCatchAllRisk()
    {
        var history = new HistoricalSignalSummary(2, 0, 0, 0, 0, 0, RandomRecipientAcceptedCount: 2);
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.Accepted,
            AcceptanceStrength.High,
            CatchAllStatus.Unknown,
            history: history));

        Assert.Equal(EmailValidationStatus.Risky, result.Status);
        Assert.Contains(ReasonCode.HistoricalCatchAllBehavior, result.ReasonCodes);
    }

    [Fact]
    public void GoogleRandomAcceptanceHistory_RemainsAmbiguous()
    {
        var history = new HistoricalSignalSummary(2, 0, 0, 2, 0, 0, RandomRecipientAcceptedCount: 2);
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.GatewayAccepted,
            AcceptanceStrength.Low,
            CatchAllStatus.Unknown,
            provider: MailProvider.GoogleWorkspace,
            history: history));

        Assert.Equal(EmailValidationStatus.LikelyValid, result.Status);
        Assert.DoesNotContain(ReasonCode.HistoricalCatchAllBehavior, result.ReasonCodes);
    }

    [Fact]
    public void ExplicitNullMx_IsDefinitivelyInvalid()
    {
        var evidence = Evidence(SmtpResponseCategory.NotAttempted, AcceptanceStrength.None, CatchAllStatus.NotAttempted);
        var nullMxDomain = evidence.Domain! with
        {
            Dns = new DnsLookupResult(DnsStatus.Success, true, [], false, TimeSpan.Zero, ExplicitNullMx: true)
        };

        var result = _classifier.Classify(evidence with { Domain = nullMxDomain });

        Assert.Equal(EmailValidationStatus.Invalid, result.Status);
        Assert.Contains(ReasonCode.NullMailExchanger, result.ReasonCodes);
    }

    private static EmailClassificationEvidence Evidence(
        SmtpResponseCategory category,
        AcceptanceStrength strength,
        CatchAllStatus catchAll,
        double catchAllConfidence = 0.30,
        MailProvider provider = MailProvider.GenericSmtp,
        HistoricalSignalSummary? history = null)
    {
        var domain = new DomainIntelligence
        {
            Domain = "example.com",
            DomainExists = true,
            Dns = new DnsLookupResult(DnsStatus.Success, true, [new MxRecord(10, "mx.example.com")], false, TimeSpan.Zero),
            Provider = new ProviderDetectionResult(provider, 0.95),
            CatchAll = new CatchAllDetectionResult(catchAll, 1, 0, 1, 0, Confidence: catchAllConfidence),
            ObservedAt = DateTimeOffset.UtcNow
        };
        var probeStatus = category switch
        {
            SmtpResponseCategory.Accepted or SmtpResponseCategory.GatewayAccepted => SmtpMailboxStatus.Accepted,
            SmtpResponseCategory.RecipientRejected => SmtpMailboxStatus.Rejected,
            SmtpResponseCategory.VerificationBlocked => SmtpMailboxStatus.Blocked,
            SmtpResponseCategory.Greylisted or SmtpResponseCategory.RateLimited or SmtpResponseCategory.TemporaryFailure => SmtpMailboxStatus.TemporaryFailure,
            _ => SmtpMailboxStatus.Unknown
        };
        SmtpEvidence? smtpEvidence = null;
        SmtpSessionEvidence? session = null;
        if (category == SmtpResponseCategory.RecipientRejected)
        {
            smtpEvidence = new SmtpEvidence(
                SmtpCommand.RcptTo, 550, "5.1.1", category,
                SmtpResponseTextClassification.RecipientDoesNotExist, 1, provider,
                "mx.example.com", 1, DateTimeOffset.UtcNow);
            session = new SmtpSessionEvidence(
                SmtpCommand.RcptTo,
                [
                    new(SmtpCommand.MailFrom, 250, "2.1.0", SmtpResponseCategory.Accepted,
                        SmtpResponseTextClassification.Success, TimeSpan.Zero),
                    new(SmtpCommand.RcptTo, 550, "5.1.1", category,
                        SmtpResponseTextClassification.RecipientDoesNotExist, TimeSpan.Zero)
                ],
                "mx.example.com", TimeSpan.Zero, "probe@validator.example");
        }
        var probe = new SmtpProbeResult(
            probeStatus, null, null, TimeSpan.Zero,
            Evidence: smtpEvidence, SessionEvidence: session);
        var providerResult = new ProviderValidationResult(provider, 0.95, category, strength, [], "Test evidence");
        return new(
            true,
            DnsStatus.Success,
            domain,
            false,
            new MailboxEvidence("example.com", "mx.example.com", probe, providerResult),
            history ?? HistoricalSignalSummary.Empty);
    }
}
