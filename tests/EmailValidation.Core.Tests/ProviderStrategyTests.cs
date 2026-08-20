using EmailValidation.Core;

namespace EmailValidation.Core.Tests;

public sealed class ProviderStrategyTests
{
    [Fact]
    public async Task GoogleAcceptance_IsGatewayAcceptanceNotDefinitiveMailboxProof()
    {
        var context = Context(MailProvider.GoogleWorkspace, SmtpResponseCategory.Accepted, CatchAllStatus.Unknown);

        var result = await new GoogleWorkspaceStrategy().EvaluateAsync(context);

        Assert.Equal(SmtpResponseCategory.GatewayAccepted, result.EffectiveCategory);
        Assert.Equal(AcceptanceStrength.Low, result.AcceptanceStrength);
        Assert.Contains(ReasonCode.MailboxAcceptanceAmbiguous, result.ReasonCodes);
    }

    [Fact]
    public async Task GenericAcceptanceWithRandomRejection_IsStrongEvidence()
    {
        var context = Context(MailProvider.GenericSmtp, SmtpResponseCategory.Accepted, CatchAllStatus.NotCatchAll);

        var result = await new GenericSmtpStrategy().EvaluateAsync(context);

        Assert.Equal(SmtpResponseCategory.Accepted, result.EffectiveCategory);
        Assert.Equal(AcceptanceStrength.High, result.AcceptanceStrength);
    }

    [Theory]
    [InlineData(MailProvider.Microsoft365)]
    [InlineData(MailProvider.Proofpoint)]
    [InlineData(MailProvider.Mimecast)]
    public async Task GatewayProviders_DoNotPromoteAcceptanceToStrongEvidence(MailProvider provider)
    {
        IMailProviderStrategy strategy = provider switch
        {
            MailProvider.Microsoft365 => new Microsoft365Strategy(),
            MailProvider.Proofpoint => new ProofpointStrategy(),
            _ => new MimecastStrategy()
        };

        var result = await strategy.EvaluateAsync(Context(provider, SmtpResponseCategory.Accepted, CatchAllStatus.Unknown));

        Assert.Equal(SmtpResponseCategory.GatewayAccepted, result.EffectiveCategory);
        Assert.NotEqual(AcceptanceStrength.High, result.AcceptanceStrength);
    }

    [Fact]
    public async Task MicrosoftTargetAcceptedAndRandomRejected_IsStrongDifferentiatedEvidence()
    {
        var context = Context(MailProvider.Microsoft365, SmtpResponseCategory.Accepted, CatchAllStatus.LikelyNotCatchAll);
        var result = await new Microsoft365Strategy().EvaluateAsync(context);

        Assert.Equal(SmtpResponseCategory.Accepted, result.EffectiveCategory);
        Assert.Equal(AcceptanceStrength.High, result.AcceptanceStrength);
        Assert.Equal(MailProvider.Microsoft365, result.MailboxProvider);
        Assert.Equal(VerificationReliabilityLevel.High, result.VerificationReliabilityLevel);
        Assert.Equal(EmailValidationStatus.Valid, Classify(context, result).Status);
    }

    [Fact]
    public async Task MicrosoftTargetAcceptedAndRandomAccepted_IsAmbiguousGatewayEvidence()
    {
        var context = Context(MailProvider.Microsoft365, SmtpResponseCategory.Accepted, CatchAllStatus.LikelyCatchAll);
        var result = await new Microsoft365Strategy().EvaluateAsync(context);

        Assert.Equal(SmtpResponseCategory.GatewayAccepted, result.EffectiveCategory);
        Assert.Equal(AcceptanceStrength.Low, result.AcceptanceStrength);
        Assert.Equal(MailProvider.Unknown, result.MailboxProvider);
        Assert.Equal(VerificationReliabilityLevel.Low, result.VerificationReliabilityLevel);
        Assert.Contains(ReasonCode.MailboxAcceptanceAmbiguous, result.ReasonCodes);
        Assert.Equal(EmailValidationStatus.Risky, Classify(context, result).Status);
    }

    [Fact]
    public async Task MicrosoftRecipientSpecificRejection_IsStrongRejectionEvidence()
    {
        var context = Context(MailProvider.Microsoft365, SmtpResponseCategory.RecipientRejected, CatchAllStatus.LikelyNotCatchAll);
        var result = await new Microsoft365Strategy().EvaluateAsync(context);

        Assert.Equal(SmtpResponseCategory.RecipientRejected, result.EffectiveCategory);
        Assert.Equal(VerificationReliabilityLevel.High, result.VerificationReliabilityLevel);
        Assert.Contains(ReasonCode.MicrosoftRecipientRejected, result.ReasonCodes);
        Assert.Equal(EmailValidationStatus.Invalid, Classify(context, result).Status);
    }

    [Theory]
    [InlineData(SmtpResponseCategory.TemporaryFailure)]
    [InlineData(SmtpResponseCategory.VerificationBlocked)]
    [InlineData(SmtpResponseCategory.RateLimited)]
    public async Task MicrosoftAmbiguousFailures_DoNotBecomeRecipientRejections(SmtpResponseCategory category)
    {
        var context = Context(MailProvider.Microsoft365, category, CatchAllStatus.Unknown);
        var result = await new Microsoft365Strategy().EvaluateAsync(context);

        Assert.Equal(category, result.EffectiveCategory);
        Assert.DoesNotContain(ReasonCode.MailboxRejected, result.ReasonCodes);
        Assert.Equal(EmailValidationStatus.Unknown, Classify(context, result).Status);
    }

    [Fact]
    public async Task MailFromRejected_NeverBecomesMailboxRejection()
    {
        var original = Context(MailProvider.GenericSmtp, SmtpResponseCategory.VerificationBlocked, CatchAllStatus.Unknown);
        var mailFromEvidence = new SmtpEvidence(
            SmtpCommand.MailFrom, 550, "5.7.1", SmtpResponseCategory.VerificationBlocked,
            SmtpResponseTextClassification.PolicyRejection, 10, MailProvider.GenericSmtp,
            "mx.reliantquotes.test", 1, DateTimeOffset.UtcNow, "550 5.7.1 Sender rejected");
        var session = new SmtpSessionEvidence(
            SmtpCommand.MailFrom,
            [
                new(SmtpCommand.Ehlo, 250, null, SmtpResponseCategory.Accepted,
                    SmtpResponseTextClassification.Success, TimeSpan.Zero),
                new(SmtpCommand.MailFrom, 550, "5.7.1", SmtpResponseCategory.VerificationBlocked,
                    SmtpResponseTextClassification.PolicyRejection, TimeSpan.Zero)
            ],
            "mx.reliantquotes.test", TimeSpan.Zero, "probe@validator.example");
        var context = original with
        {
            MailboxProbe = new SmtpProbeResult(
                SmtpMailboxStatus.Blocked, 550, "Sender rejected", TimeSpan.Zero,
                Evidence: mailFromEvidence, SessionEvidence: session)
        };

        var provider = await new GenericSmtpStrategy().EvaluateAsync(context);
        var result = Classify(context, provider);

        Assert.Equal(SmtpResponseCategory.VerificationBlocked, provider.EffectiveCategory);
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(ReasonCode.SenderIdentityRejected, provider.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.MailboxRejected, result.ReasonCodes);
        Assert.Null(session.RcptTo);
    }

    [Fact]
    public void Resolver_UsesGenericStrategyAsFallback()
    {
        IMailProviderStrategy[] strategies = [new Microsoft365Strategy(), new GenericSmtpStrategy()];
        var resolver = new MailProviderStrategyResolver(strategies);

        Assert.IsType<GenericSmtpStrategy>(resolver.Resolve(new ProviderDetectionResult(MailProvider.Unknown, 0)));
    }

    private static ProviderValidationContext Context(
        MailProvider provider,
        SmtpResponseCategory category,
        CatchAllStatus catchAll)
    {
        var catchAllEvidence = new CatchAllDetectionResult(catchAll, 1, 0, 1, 0, Confidence: 0.85);
        var domain = new DomainIntelligence
        {
            Domain = "example.com",
            DomainExists = true,
            Dns = new DnsLookupResult(DnsStatus.Success, true, [new MxRecord(10, "mx.example.com")], false, TimeSpan.Zero),
            Provider = new ProviderDetectionResult(
                provider,
                0.97,
                GatewayProvider: provider == MailProvider.Microsoft365
                    ? GatewayProvider.MicrosoftExchangeOnlineProtection
                    : GatewayProvider.Unknown),
            CatchAll = catchAllEvidence,
            ObservedAt = DateTimeOffset.UtcNow
        };
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 250, "2.1.5", category, SmtpResponseTextClassification.Success,
            10, provider, "mx.example.com", 1, DateTimeOffset.UtcNow);
        var stages = new List<SmtpStageResult>
        {
            new(SmtpCommand.MailFrom, 250, "2.1.0", SmtpResponseCategory.Accepted,
                SmtpResponseTextClassification.Success, TimeSpan.Zero),
            new(SmtpCommand.RcptTo, category == SmtpResponseCategory.RecipientRejected ? 550 : 250,
                category == SmtpResponseCategory.RecipientRejected ? "5.1.1" : "2.1.5", category,
                category == SmtpResponseCategory.RecipientRejected
                    ? SmtpResponseTextClassification.RecipientDoesNotExist
                    : SmtpResponseTextClassification.Success, TimeSpan.Zero)
        };
        var session = new SmtpSessionEvidence(
            category == SmtpResponseCategory.Accepted ? null : SmtpCommand.RcptTo,
            stages, "mx.example.com", TimeSpan.Zero, "probe@validator.example");
        var probe = new SmtpProbeResult(
            category == SmtpResponseCategory.RecipientRejected ? SmtpMailboxStatus.Rejected : SmtpMailboxStatus.Accepted,
            evidence.ResponseCode, "OK", TimeSpan.Zero, Evidence: evidence, SessionEvidence: session);
        return new(domain, probe, HistoricalSignalSummary.Empty);
    }

    private static ClassificationResult Classify(
        ProviderValidationContext context,
        ProviderValidationResult providerResult) => new EmailClassificationEngine().Classify(
            new EmailClassificationEvidence(
                true,
                DnsStatus.Success,
                context.Domain,
                false,
                new MailboxEvidence(
                    context.Domain.Domain,
                    context.Domain.Dns.MxRecords[0].Host,
                    context.MailboxProbe,
                    providerResult),
                context.History));
}
