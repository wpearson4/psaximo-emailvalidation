using EmailValidation.Core;
using EmailValidation.Infrastructure;

namespace EmailValidation.Core.Tests;

public sealed class HistoricalObservationTests
{
    [Fact]
    public async Task StoreAndAggregator_ProduceNonSensitiveDomainSignals()
    {
        var store = new InMemoryValidationObservationStore();
        await store.RecordAsync(Observation(CatchAllStatus.LikelyCatchAll, SmtpResponseCategory.Accepted));
        await store.RecordAsync(Observation(CatchAllStatus.Unknown, SmtpResponseCategory.VerificationBlocked));
        await store.RecordAsync(Observation(CatchAllStatus.Unknown, SmtpResponseCategory.RateLimited));

        var observations = await store.GetDomainObservationsAsync("EXAMPLE.COM");
        var result = new HistoricalSignalAggregator().Aggregate(observations);

        Assert.Equal(3, result.ObservationCount);
        Assert.Equal(1, result.LikelyCatchAllCount);
        Assert.Equal(1, result.VerificationBlockedCount);
        Assert.Equal(1, result.RateLimitedCount);
        Assert.Equal(1, result.RandomRecipientAcceptedCount);
    }

    [Theory]
    [InlineData(false, 0.0, 1.0, VerificationReliabilityLevel.High)]
    [InlineData(true, 1.0, 0.0, VerificationReliabilityLevel.Low)]
    public void Aggregator_LearnsTenantSpecificMicrosoftBehavior(
        bool randomAccepted,
        double expectedRandomAcceptanceRate,
        double expectedRandomRejectionRate,
        VerificationReliabilityLevel expectedReliability)
    {
        var observations = new List<ValidationObservation>();
        for (var index = 0; index < 10; index++)
        {
            observations.Add(new ValidationObservation(
                "example.com", ValidationObservationType.MailboxProbe, MailProvider.Microsoft365,
                "tenant.mail.protection.outlook.com", CatchAllStatus.Unknown, 0,
                randomAccepted ? SmtpResponseCategory.GatewayAccepted : SmtpResponseCategory.Accepted,
                DateTimeOffset.UtcNow, 10,
                GatewayProvider: GatewayProvider.MicrosoftExchangeOnlineProtection,
                TopologyFingerprint: "0:tenant.mail.protection.outlook.com"));
            observations.Add(new ValidationObservation(
                "example.com", ValidationObservationType.CatchAllProbe, MailProvider.Microsoft365,
                "tenant.mail.protection.outlook.com",
                randomAccepted ? CatchAllStatus.LikelyCatchAll : CatchAllStatus.LikelyNotCatchAll,
                0.95,
                randomAccepted ? SmtpResponseCategory.Accepted : SmtpResponseCategory.RecipientRejected,
                DateTimeOffset.UtcNow, 10,
                RandomRecipientAcceptedCount: randomAccepted ? 1 : 0,
                RandomRecipientProbeCount: 1,
                RandomRecipientRejectedCount: randomAccepted ? 0 : 1,
                GatewayProvider: GatewayProvider.MicrosoftExchangeOnlineProtection,
                TopologyFingerprint: "0:tenant.mail.protection.outlook.com"));
        }

        var result = new HistoricalSignalAggregator().Aggregate(observations);

        Assert.Equal(1.0, result.TargetAcceptanceRate);
        Assert.Equal(expectedRandomAcceptanceRate, result.RandomAcceptanceRate);
        Assert.Equal(expectedRandomRejectionRate, 1 - result.RandomAcceptanceRate);
        Assert.Equal(expectedReliability, result.VerificationReliabilityLevel);
    }

    private static ValidationObservation Observation(CatchAllStatus catchAll, SmtpResponseCategory category) => new(
        "example.com", ValidationObservationType.CatchAllProbe, MailProvider.GenericSmtp,
        "mx.example.com", catchAll, 0.85, category, DateTimeOffset.UtcNow, 10,
        catchAll == CatchAllStatus.LikelyCatchAll ? 1 : 0);
}
