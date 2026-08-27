using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class AdvancedIntelligenceTests
{
    [Theory]
    [InlineData("gmal.com", "gmail.com")]
    [InlineData("hotmial.com", "hotmail.com")]
    [InlineData("outlok.com", "outlook.com")]
    public void TypoDetector_ReturnsSeparateHighConfidenceSuggestion(string input, string expected)
    {
        var detector = new EmailTypoDetector(DefaultOptions());

        var result = detector.Detect("will", input);

        Assert.True(result.TypoDetected);
        Assert.Equal(expected, result.SuggestedDomain);
        Assert.Equal($"will@{expected}", result.SuggestedEmail);
        Assert.True(result.Confidence >= 0.90);
    }

    [Fact]
    public void TypoDetector_DoesNotSuggestAnArbitraryDomain()
    {
        var detector = new EmailTypoDetector(DefaultOptions());

        var result = detector.Detect("person", "example-company.com");

        Assert.False(result.TypoDetected);
        Assert.Null(result.SuggestedEmail);
    }

    [Theory]
    [InlineData("gmail.com", true)]
    [InlineData("outlook.com", true)]
    [InlineData("appendpros.com", false)]
    public void FreeEmailDetector_IsInformationalAndConfigurable(string domain, bool expected)
    {
        var detector = new FreeEmailProviderDetector(DefaultOptions());

        Assert.Equal(expected, detector.IsFreeProvider(domain));
    }

    [Theory]
    [InlineData("webmaster", true)]
    [InlineData("support+website", true)]
    [InlineData("will", false)]
    public void RoleDetector_RecognizesExpandedAndPlusAddressedRoles(string localPart, bool expected)
    {
        var detector = new RoleAccountDetector(DefaultOptions());

        Assert.Equal(expected, detector.IsRoleAccount(localPart));
    }

    [Fact]
    public async Task ConfiguredIntelligence_IsExplicitlyAttributed()
    {
        var settings = new EmailValidationOptions
        {
            Intelligence = new IntelligenceOptions
            {
                ToxicDomains = ["toxic.test"],
                KnownSpamTrapAddresses = ["trap@toxic.test"],
                AbuseRiskAddresses = ["abuse@toxic.test"],
                SuppressedAddresses = new(StringComparer.OrdinalIgnoreCase) { ["stop@toxic.test"] = "HardBounce" },
                MxForwardingSuffixes = new(StringComparer.OrdinalIgnoreCase) { ["forward.test"] = "ConfiguredForwarder" }
            }
        };
        var options = Options.Create(settings);

        var toxic = await new ToxicDomainDetector(options).EvaluateAsync("toxic.test");
        var trap = await new SpamTrapRiskDetector(options).EvaluateAsync("trap@toxic.test");
        var abuse = await new AbuseRiskProvider(options).EvaluateAsync("abuse@toxic.test");
        var suppression = await new SuppressionIntelligenceProvider(options).EvaluateAsync("stop@toxic.test");
        var forward = new MxForwardDetector(options).Evaluate(
            "customer.test", [new MxRecord(10, "mx.forward.test")]);

        Assert.Equal(ToxicDomainStatus.KnownToxic, toxic.Status);
        Assert.Equal(SpamTrapRiskStatus.KnownSpamTrap, trap.Status);
        Assert.Equal(AbuseRiskStatus.KnownRisk, abuse.Status);
        Assert.Equal(SuppressionStatus.Suppressed, suppression.Status);
        Assert.Equal(MxForwardStatus.ConfirmedForwarding, forward.Status);
        Assert.All(
            new EvidenceSource?[] { toxic.EvidenceSource, trap.EvidenceSource, abuse.EvidenceSource, suppression.EvidenceSource, forward.EvidenceSource },
            source => Assert.Equal(EvidenceSource.ConfiguredIntelligenceProvider, source));
    }

    [Fact]
    public void DisposableDetector_RecognizesSubdomainsWithoutClaimingCleanForUnknownDomains()
    {
        var detector = new DisposableEmailDetector(DefaultOptions());

        Assert.Equal(DisposableDomainStatus.KnownDisposable, detector.Evaluate("sub.mailinator.com").Status);
        Assert.Equal(DisposableDomainStatus.Unknown, detector.Evaluate("appendpros.com").Status);
    }

    [Theory]
    [InlineData(552, "552 5.2.2 Mailbox full")]
    [InlineData(452, "452 4.2.2 Mailbox over quota")]
    public void SmtpClassifier_PreservesMailboxFullAsExistingButTemporarilyUndeliverable(int code, string response)
    {
        var evidence = new SmtpResponseClassifier().Classify(
            SmtpCommand.RcptTo, code, response, TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.test");

        Assert.Equal(SmtpResponseCategory.MailboxFull, evidence.Category);
        Assert.Equal(SmtpResponseTextClassification.MailboxFull, evidence.TextClassification);
        Assert.Equal(SmtpMailboxStatus.MailboxFull,
            SmtpResponseClassifier.ToProbeResult(evidence, TimeSpan.Zero).Status);
    }

    [Fact]
    public void Classification_MailboxFullIsRiskyNotInvalid()
    {
        var domain = Domain(CatchAllStatus.NotCatchAll);
        var provider = Provider(SmtpResponseCategory.MailboxFull, "Mailbox is full.", 0.85);
        var result = new EmailClassificationEngine().Classify(new EmailClassificationEvidence(
            true,
            DnsStatus.Success,
            domain,
            false,
            new MailboxEvidence(domain.Domain, "mx.example.test",
                new SmtpProbeResult(SmtpMailboxStatus.MailboxFull, 552, "5.2.2", TimeSpan.Zero), provider),
            HistoricalSignalSummary.Empty));

        Assert.Equal(EmailValidationStatus.Risky, result.Status);
        Assert.Contains(ReasonCode.MailboxFull, result.ReasonCodes);
    }

    [Fact]
    public void ResultEvaluation_SeparatesTechnicalValidityFromDoNotMailPolicy()
    {
        var domain = Domain(CatchAllStatus.LikelyCatchAll);
        var checks = new EmailValidationChecks
        {
            SyntaxValid = true,
            DomainExists = true,
            MxPresent = true,
            RoleAccount = true,
            CatchAll = CatchAllStatus.LikelyCatchAll,
            Mailbox = SmtpMailboxStatus.Accepted
        };
        var provider = Provider(SmtpResponseCategory.Accepted, "Accepted.", 0.90);

        var result = new ResultEvaluator().Evaluate(
            EmailValidationStatus.Risky,
            checks,
            domain,
            new EmailAddressIntelligence { Email = "support@example.test" },
            provider,
            new SmtpEvidence(
                SmtpCommand.RcptTo, 250, "2.1.5", SmtpResponseCategory.Accepted,
                SmtpResponseTextClassification.Success, 1, MailProvider.GenericSmtp,
                "mx.example.test", 1, DateTimeOffset.UtcNow),
            HistoricalSignalSummary.Empty);

        Assert.Contains(DetailedStatus.RoleBasedCatchAll, result.DetailedStatuses);
        Assert.False(result.Recommendation.Send);
        Assert.Contains("RoleBasedCatchAll", result.Recommendation.Reasons);
        Assert.Equal(BounceRisk.Moderate, result.Risk.BounceRisk);
    }

    [Fact]
    public void ResultEvaluation_DoesNotCallGatewayAcceptanceMailboxAcceptance()
    {
        var domain = Domain(CatchAllStatus.Unknown);
        var provider = Provider(SmtpResponseCategory.GatewayAccepted, "Gateway only.", 0.20);

        var result = new ResultEvaluator().Evaluate(
            EmailValidationStatus.LikelyValid,
            new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true },
            domain,
            new EmailAddressIntelligence { Email = "person@example.test" },
            provider,
            null,
            HistoricalSignalSummary.Empty);

        Assert.DoesNotContain(DetailedStatus.MailboxAccepted, result.DetailedStatuses);
        Assert.Equal(BounceRisk.Moderate, result.Risk.BounceRisk);
    }

    [Fact]
    public void HistoricalIntelligence_ExposesGreylistingProbability()
    {
        var observations = new[]
        {
            Observation(SmtpResponseCategory.Greylisted),
            Observation(SmtpResponseCategory.Greylisted),
            Observation(SmtpResponseCategory.Accepted),
            Observation(SmtpResponseCategory.RecipientRejected)
        };

        var result = new HistoricalSignalAggregator().Aggregate(observations);

        Assert.Equal(2, result.GreylistedCount);
        Assert.Equal(0.5, result.GreylistingProbability);
    }

    private static IOptions<EmailValidationOptions> DefaultOptions() => Options.Create(new EmailValidationOptions());

    private static DomainIntelligence Domain(CatchAllStatus catchAll) => new()
    {
        Domain = "example.test",
        DomainExists = true,
        Dns = new DnsLookupResult(
            DnsStatus.Success, true, [new MxRecord(10, "mx.example.test")], false, TimeSpan.Zero),
        Provider = new ProviderDetectionResult(MailProvider.GenericSmtp, 0.55),
        MailInfrastructure = new MailInfrastructureResult(
            MailInfrastructureStatus.Routable, ["mx.example.test"], [], 0.95),
        CatchAll = new CatchAllDetectionResult(catchAll, 1, catchAll == CatchAllStatus.LikelyCatchAll ? 1 : 0,
            catchAll == CatchAllStatus.NotCatchAll ? 1 : 0, 0, Confidence: 0.90),
        ObservedAt = DateTimeOffset.UtcNow
    };

    private static ProviderValidationResult Provider(
        SmtpResponseCategory category,
        string explanation,
        double reliability) => new(
            MailProvider.GenericSmtp,
            0.55,
            category,
            category == SmtpResponseCategory.Accepted ? AcceptanceStrength.High : AcceptanceStrength.None,
            [],
            explanation,
            VerificationReliability: reliability,
            VerificationReliabilityLevel: reliability >= 0.8
                ? EmailValidation.Core.VerificationReliabilityLevel.High
                : EmailValidation.Core.VerificationReliabilityLevel.Low);

    private static ValidationObservation Observation(SmtpResponseCategory category) => new(
        "example.test",
        ValidationObservationType.MailboxProbe,
        MailProvider.GenericSmtp,
        "mx.example.test",
        CatchAllStatus.Unknown,
        0,
        category,
        DateTimeOffset.UtcNow,
        1);
}
