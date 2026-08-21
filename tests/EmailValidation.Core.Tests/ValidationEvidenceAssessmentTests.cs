using EmailValidation.Core;

namespace EmailValidation.Core.Tests;

public sealed class ValidationEvidenceAssessmentTests
{
    [Fact]
    public void LocalCooldown_IsNotAttemptedEvidence()
    {
        var domain = Domain(CatchAllStatus.Unknown);
        var probe = new SmtpProbeResult(
            SmtpMailboxStatus.NotAttempted, null, null, TimeSpan.Zero, 0,
            Evidence(SmtpResponseCategory.LocalCooldown))
        {
            Disposition = SmtpProbeDisposition.LocalCooldown,
            RetryAfter = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var provider = Provider(SmtpResponseCategory.LocalCooldown);

        Assert.Equal(EvidenceQuality.NotAttempted,
            ValidationEvidenceAssessment.Quality(EmailValidationStatus.Unknown, domain, probe, provider));
    }

    [Fact]
    public void AmbiguousGatewayCatchAll_HasDedicatedSubtype()
    {
        var domain = Domain(CatchAllStatus.Unknown);
        var provider = Provider(SmtpResponseCategory.GatewayAccepted);

        Assert.Equal(CatchAllClassification.GatewayAmbiguous,
            ValidationEvidenceAssessment.CatchAllType(
                EmailValidationStatus.CatchAll, domain, provider, HistoricalSignalSummary.Empty));
    }

    private static DomainIntelligence Domain(CatchAllStatus catchAll) => new()
    {
        Domain = "example.test",
        DomainExists = true,
        Dns = new DnsLookupResult(
            DnsStatus.Success, true, [new MxRecord(0, "mx.example.test")], false, TimeSpan.Zero),
        Provider = new ProviderDetectionResult(MailProvider.Microsoft365, 0.95),
        CatchAll = new CatchAllDetectionResult(catchAll, 0, 0, 0, 0, Confidence: 0.2),
        ObservedAt = DateTimeOffset.UtcNow
    };

    private static ProviderValidationResult Provider(SmtpResponseCategory category) => new(
        MailProvider.Microsoft365, 0.95, category, AcceptanceStrength.None, [], "test");

    private static SmtpEvidence Evidence(SmtpResponseCategory category) => new(
        SmtpCommand.Connect, null, null, category,
        SmtpResponseTextClassification.VerificationUnavailable, 0,
        MailProvider.Microsoft365, "mx.example.test", 0, DateTimeOffset.UtcNow);
}
