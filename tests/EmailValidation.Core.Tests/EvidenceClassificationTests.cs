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
    public void CatchAllAcceptance_HasDedicatedCatchAllStatus()
    {
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.GatewayAccepted,
            AcceptanceStrength.Low,
            CatchAllStatus.LikelyCatchAll,
            catchAllConfidence: 0.90));

        Assert.Equal(EmailValidationStatus.CatchAll, result.Status);
        Assert.Contains(ReasonCode.CatchAllLikely, result.ReasonCodes);
    }

    [Fact]
    public void CatchAllStatus_TakesPrecedenceOverOtherRiskFlags()
    {
        var evidence = Evidence(
            SmtpResponseCategory.Accepted,
            AcceptanceStrength.Medium,
            CatchAllStatus.LikelyCatchAll,
            catchAllConfidence: 0.90);
        var result = _classifier.Classify(evidence with
        {
            RoleAccount = true,
            Domain = evidence.Domain! with { Disposable = true },
            AddressIntelligence = new EmailAddressIntelligence
            {
                Email = "support@example.com",
                Typo = new TypoDetectionResult(true, "example.org", "support@example.org", 0.95)
            }
        });

        Assert.Equal(EmailValidationStatus.CatchAll, result.Status);
        Assert.Contains(ReasonCode.RoleAccount, result.ReasonCodes);
        Assert.Contains(ReasonCode.DisposableDomain, result.ReasonCodes);
        Assert.Contains(ReasonCode.TypoDetected, result.ReasonCodes);
    }

    [Fact]
    public void GatewayAcceptanceWithoutCatchAllProof_IsUnknownNotCatchAll()
    {
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.GatewayAccepted,
            AcceptanceStrength.Low,
            CatchAllStatus.Unknown,
            provider: MailProvider.Microsoft365));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.NotEqual(EmailValidationStatus.CatchAll, result.Status);
        Assert.Contains(ReasonCode.MailboxAcceptanceAmbiguous, result.ReasonCodes);
    }

    [Fact]
    public void GatewayAcceptanceWithNegativeCatchAllEvidence_RemainsLikelyValid()
    {
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.GatewayAccepted,
            AcceptanceStrength.Medium,
            CatchAllStatus.LikelyNotCatchAll,
            catchAllConfidence: 0.85,
            provider: MailProvider.GoogleWorkspace));

        Assert.Equal(EmailValidationStatus.LikelyValid, result.Status);
        Assert.DoesNotContain(ReasonCode.CatchAllGatewayAmbiguous, result.ReasonCodes);
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
    [InlineData(SmtpResponseCategory.LocalCooldown, ReasonCode.LocalCooldown)]
    public void AmbiguousOrTemporaryEvidence_IsUnknown(SmtpResponseCategory category, ReasonCode reason)
    {
        var result = _classifier.Classify(Evidence(category, AcceptanceStrength.None, CatchAllStatus.Unknown));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(reason, result.ReasonCodes);
    }

    [Fact]
    public void HistoricalWeakCatchAllEvidence_DoesNotCreateCatchAllStatus()
    {
        var history = new HistoricalSignalSummary(3, 2, 0, 1, 0, 0);
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.Accepted,
            AcceptanceStrength.High,
            CatchAllStatus.Unknown,
            history: history));

        Assert.NotEqual(EmailValidationStatus.CatchAll, result.Status);
    }

    [Fact]
    public void UncorrelatedHistoricalRandomAcceptance_DoesNotEstablishAcceptAll()
    {
        var history = new HistoricalSignalSummary(2, 0, 0, 0, 0, 0, RandomRecipientAcceptedCount: 2);
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.Accepted,
            AcceptanceStrength.High,
            CatchAllStatus.Unknown,
            history: history));

        Assert.NotEqual(EmailValidationStatus.CatchAll, result.Status);
        Assert.Equal(EmailValidationStatus.LikelyValid, result.Status);
        Assert.DoesNotContain(ReasonCode.AcceptAllObserved, result.ReasonCodes);
    }

    [Fact]
    public void GoogleGatewayAcceptance_RemainsUnknownWithoutCatchAllProof()
    {
        var history = new HistoricalSignalSummary(2, 0, 0, 2, 0, 0, RandomRecipientAcceptedCount: 2);
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.GatewayAccepted,
            AcceptanceStrength.Low,
            CatchAllStatus.Unknown,
            provider: MailProvider.GoogleWorkspace,
            history: history));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.DoesNotContain(ReasonCode.HistoricalCatchAllBehavior, result.ReasonCodes);
    }

    [Theory]
    [InlineData("yahoo.com")]
    [InlineData("aol.com")]
    public void YahooAndAolTargetPlusRandomAcceptance_IsAcceptAllNotCatchAll(string domain)
    {
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.Accepted,
            AcceptanceStrength.Medium,
            CatchAllStatus.Unknown,
            provider: MailProvider.Yahoo,
            recipientBehavior: DomainRecipientBehavior.AcceptAll,
            domainName: domain));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.NotEqual(EmailValidationStatus.CatchAll, result.Status);
        Assert.Contains(ReasonCode.AcceptAllObserved, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.MailboxAcceptanceAmbiguous, result.ReasonCodes);
    }

    [Fact]
    public void OneSessionAcceptAllCandidate_RemainsUnknownPendingConfirmation()
    {
        var evidence = Evidence(
            SmtpResponseCategory.Accepted,
            AcceptanceStrength.High,
            CatchAllStatus.Unknown);
        evidence = evidence with
        {
            Domain = evidence.Domain! with
            {
                CatchAll = new CatchAllDetectionResult(
                    CatchAllStatus.Unknown, 2, 2, 0, 0, "Candidate", .75)
                {
                    ReasonCode = CatchAllReasonCode.AcceptAllCandidate,
                    RecipientBehavior = DomainRecipientBehavior.Unknown,
                    IndependentObservationCount = 1,
                    RefreshInconclusive = true
                }
            }
        };

        var result = _classifier.Classify(evidence);

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(ReasonCode.AcceptAllCandidate, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.AcceptAllObserved, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.CatchAllDetected, result.ReasonCodes);
    }

    [Fact]
    public void ExplicitAcceptAllBehavior_OverridesLegacyCatchAllStatus()
    {
        var result = _classifier.Classify(Evidence(
            SmtpResponseCategory.LocalCooldown,
            AcceptanceStrength.None,
            CatchAllStatus.LikelyCatchAll,
            catchAllConfidence: 0.90,
            recipientBehavior: DomainRecipientBehavior.AcceptAll));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(ReasonCode.AcceptAllObserved, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.CatchAllDetected, result.ReasonCodes);
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
        HistoricalSignalSummary? history = null,
        DomainRecipientBehavior recipientBehavior = DomainRecipientBehavior.Unknown,
        string domainName = "example.com")
    {
        var domain = new DomainIntelligence
        {
            Domain = domainName,
            DomainExists = true,
            Dns = new DnsLookupResult(DnsStatus.Success, true, [new MxRecord(10, "mx.example.com")], false, TimeSpan.Zero),
            Provider = new ProviderDetectionResult(provider, 0.95),
            CatchAll = new CatchAllDetectionResult(
                catchAll,
                recipientBehavior == DomainRecipientBehavior.AcceptAll ? 2 : 1,
                recipientBehavior == DomainRecipientBehavior.AcceptAll ? 2 : 0,
                recipientBehavior == DomainRecipientBehavior.RecipientSpecific ? 1 : 0,
                0,
                Confidence: catchAllConfidence)
            {
                RecipientBehavior = recipientBehavior != DomainRecipientBehavior.Unknown
                    ? recipientBehavior
                    : catchAll == CatchAllStatus.LikelyCatchAll
                        ? DomainRecipientBehavior.CatchAll
                        : DomainRecipientBehavior.Unknown,
                ReasonCode = recipientBehavior == DomainRecipientBehavior.AcceptAll
                    ? CatchAllReasonCode.AcceptAllConfirmed
                    : catchAll == CatchAllStatus.LikelyCatchAll
                        ? CatchAllReasonCode.IndependentRoutingEvidence
                        : CatchAllReasonCode.None,
                IndependentObservationCount = recipientBehavior == DomainRecipientBehavior.AcceptAll ? 2 : 0,
                EvidenceContractVersion = recipientBehavior == DomainRecipientBehavior.AcceptAll
                    ? CatchAllDetectionResult.CurrentRecipientBehaviorEvidenceContractVersion
                    : null
            },
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
