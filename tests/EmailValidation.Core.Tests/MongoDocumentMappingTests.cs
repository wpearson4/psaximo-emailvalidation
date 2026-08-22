using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace EmailValidation.Core.Tests;

public sealed class MongoDocumentMappingTests
{
    [Fact]
    public void DomainDocument_MapsStructuredIntelligenceAndDropsProbePayloads()
    {
        var probe = new SmtpProbeResult(SmtpMailboxStatus.Accepted, 250, "raw response", TimeSpan.Zero);
        var domain = Domain() with
        {
            CatchAll = new CatchAllDetectionResult(
                CatchAllStatus.LikelyCatchAll, 2, 2, 0, 0,
                "Random recipients accepted.", 0.96)
            {
                ProbeResults = [probe],
                ReasonCode = CatchAllReasonCode.RandomRecipientsAccepted,
                ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                StrategyVersion = "1.1.0"
            }
        };

        var document = MongoValidationIntelligenceStore.DomainIntelligenceDocument.FromModel(domain);
        var restored = document.ToModel();

        Assert.Equal("example.test", document.Id);
        Assert.Equal(MailProvider.GenericSmtp, document.Provider);
        Assert.Equal("topology-1", document.MxTopologyFingerprint);
        Assert.Equal(CatchAllStatus.LikelyCatchAll, document.CatchAllStatus);
        Assert.Equal(0.96, document.CatchAllConfidence);
        Assert.Equal(CatchAllReasonCode.RandomRecipientsAccepted, document.CatchAllReasonCode);
        Assert.Equal("Random recipients accepted.", document.CatchAllReason);
        Assert.Equal(2, document.CatchAllEvidenceCount);
        Assert.Equal(2, document.RandomProbeAcceptedCount);
        Assert.Equal(0, document.RandomProbeRejectedCount);
        Assert.Equal("1.1.0", document.CatchAllStrategyVersion);
        Assert.NotNull(document.CatchAllObservedAt);
        Assert.NotNull(restored);
        Assert.Empty(restored!.CatchAll.ProbeResults);
        Assert.Equal(CatchAllReasonCode.RandomRecipientsAccepted, restored.CatchAll.ReasonCode);
    }

    [Fact]
    public void MailboxDocument_UsesHashedIdentityAndNeverStoresRawSmtpDiagnostics()
    {
        var result = Result() with
        {
            SmtpEvidence = new SmtpEvidence(
                SmtpCommand.RcptTo, 250, "2.1.5", SmtpResponseCategory.Accepted,
                SmtpResponseTextClassification.Success, 1, MailProvider.GenericSmtp,
                "mx.example.test", 1, DateTimeOffset.UtcNow, "raw response"),
            Diagnostics = new ValidationDiagnostics { Detail = "raw diagnostic" }
        };
        var mailbox = Mailbox(result);

        var document = MongoValidationIntelligenceStore.MailboxIntelligenceDocument.FromModel(mailbox);
        var restored = document.ToModel();

        Assert.Equal(64, document.Id.Length);
        Assert.DoesNotContain("person@example.test", document.Id, StringComparison.Ordinal);
        Assert.Equal("example.test", document.Domain);
        Assert.Equal(EmailValidationStatus.LikelyValid, document.LastStatus);
        Assert.NotNull(restored);
        Assert.Null(restored!.LastResult.SmtpEvidence);
        Assert.Null(restored.LastResult.Diagnostics!.Detail);
        Assert.DoesNotContain("raw response", document.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("raw diagnostic", document.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MongoUnavailable_ReadAndWriteDegradeWithoutLeakingConnectionDetails()
    {
        var username = $"test-user-{Guid.NewGuid():N}";
        var password = $"test-password-{Guid.NewGuid():N}";
        var connection = $"mongodb://{username}:{password}@127.0.0.1:1/?serverSelectionTimeoutMS=50&connectTimeoutMS=50";
        var options = Options.Create(new EmailValidationOptions
        {
            Persistence = new PersistenceOptions
            {
                Provider = "MongoDB",
                ConnectionString = connection,
                DatabaseName = "email-validation-tests"
            }
        });
        var logger = new CapturingLogger<MongoValidationIntelligenceStore>();
        var store = new MongoValidationIntelligenceStore(
            new MongoClient(connection),
            options,
            new ValidationPersistenceMetrics(),
            logger);

        var loaded = await store.GetMailboxAsync("person@example.test");
        await store.SaveMailboxAsync(Mailbox(Result()));

        Assert.Null(loaded);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(connection, message, StringComparison.Ordinal);
            Assert.DoesNotContain(username, message, StringComparison.Ordinal);
            Assert.DoesNotContain(password, message, StringComparison.Ordinal);
        });
    }

    private static DomainIntelligence Domain() => new()
    {
        Domain = "example.test",
        DomainExists = true,
        Dns = new DnsLookupResult(
            DnsStatus.Success,
            true,
            [new MxRecord(10, "mx.example.test")],
            false,
            TimeSpan.Zero),
        Provider = new ProviderDetectionResult(
            MailProvider.GenericSmtp,
            0.8,
            TopologyFingerprint: "topology-1"),
        CatchAll = new CatchAllDetectionResult(CatchAllStatus.NotCatchAll, 1, 0, 1, 0, Confidence: 0.9),
        ObservedAt = DateTimeOffset.UtcNow,
        EvidenceExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        StrategyVersion = "1.1.0"
    };

    private static EmailValidationResult Result() => new()
    {
        Email = "person@example.test",
        NormalizedEmail = "person@example.test",
        Status = EmailValidationStatus.LikelyValid,
        Confidence = 0.9,
        ConfidenceType = ConfidenceType.Heuristic,
        ConfidenceReason = "structured evidence",
        Checks = new EmailValidationChecks
        {
            SyntaxValid = true,
            DomainExists = true,
            MxPresent = true,
            Mailbox = SmtpMailboxStatus.Accepted,
            CatchAll = CatchAllStatus.NotCatchAll
        },
        MailProvider = MailProvider.GenericSmtp,
        Provider = new ProviderDetectionResult(
            MailProvider.GenericSmtp, 0.8, TopologyFingerprint: "topology-1"),
        ReasonCodes = [ReasonCode.MailboxAccepted],
        DetailedStatus = DetailedStatus.MailboxAccepted,
        DetailedStatuses = [DetailedStatus.MailboxAccepted],
        Metadata = new ValidationResultMetadata(
            new ValidationPolicyVersions("1.1.0", "2.2.0", "3.1.0", "1.1.0"),
            DateTimeOffset.UtcNow,
            MxTopologyFingerprint: "topology-1")
    };

    private static MailboxIntelligence Mailbox(EmailValidationResult result) => new()
    {
        NormalizedEmail = result.NormalizedEmail!,
        PreviousStatus = result.Status,
        PreviousMailboxResult = result.Checks.Mailbox,
        PreviousConfidence = result.Confidence,
        PreviousConfidenceType = result.ConfidenceType,
        LastValidatedAt = result.Metadata!.ValidatedAt,
        LastStrongPositiveEvidenceAt = result.Metadata.ValidatedAt,
        ProviderAtValidation = result.MailProvider,
        Policy = result.Metadata.Policy,
        ReasonCodes = result.ReasonCodes,
        MxTopologyFingerprint = result.Metadata.MxTopologyFingerprint,
        UsedLiveSmtp = true,
        LastResult = result
    };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
