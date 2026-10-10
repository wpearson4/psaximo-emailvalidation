using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Bson;

namespace EmailValidation.IntegrationTests;

public sealed class MongoValidationIntelligenceStoreTests
{
    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task Store_InitializesUpsertsAndSurvivesASecondInstance()
    {
        var connectionString = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var databaseName = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO_DATABASE")
            ?? "email-validation-integration-tests";
        var suffix = Guid.NewGuid().ToString("N");
        var domainCollection = $"EmailValidationDomainIntelligence_{suffix}";
        var mailboxCollection = $"EmailValidationMailboxIntelligence_{suffix}";
        var options = Options.Create(new EmailValidationOptions
        {
            Persistence = new PersistenceOptions
            {
                Enabled = true,
                Provider = "MongoDB",
                ConnectionString = connectionString,
                DatabaseName = databaseName,
                DomainCollection = domainCollection,
                MailboxCollection = mailboxCollection,
                MaximumObservationsPerDomain = 10
            }
        });
        var client = new MongoClient(connectionString);
        var database = client.GetDatabase(databaseName);

        try
        {
            var collection = database.GetCollection<BsonDocument>(mailboxCollection);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("NormalizedEmail"),
                new CreateIndexOptions { Name = "ux_mailbox_normalized", Unique = true }));
            await collection.InsertOneAsync(new BsonDocument
            {
                { "_id", "legacy-case-folded" }, { "NormalizedEmail", "person@example.test" }
            });
            var first = Store(client, options);
            await first.InitializeAsync();
            await first.InitializeAsync();
            await first.SaveDomainAsync(Domain());
            await first.RecordAsync(Observation());
            Assert.Null(await first.GetMailboxAsync("person@example.test"));
            Assert.Null(await first.GetMailboxAsync("Person@example.test"));
            await first.SaveMailboxAsync(Mailbox());
            await first.SaveMailboxAsync(Mailbox("Person@example.test"));

            var second = Store(client, options);
            var domain = await second.GetDomainAsync("example.test");
            var mailbox = await second.GetMailboxAsync("person@example.test");
            var observations = await second.GetDomainObservationsAsync("example.test");
            var domainIndexes = await (await database.GetCollection<object>(domainCollection)
                .Indexes.ListAsync()).ToListAsync();
            var mailboxIndexes = await (await database.GetCollection<object>(mailboxCollection)
                .Indexes.ListAsync()).ToListAsync();

            Assert.NotNull(domain);
            Assert.NotNull(mailbox);
            Assert.Equal("Person@example.test", (await second.GetMailboxAsync("Person@EXAMPLE.test"))!.NormalizedEmail);
            Assert.Equal(3, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            Assert.Single(observations);
            Assert.Contains(domainIndexes, index => index["name"] == "ux_domain_normalized");
            Assert.DoesNotContain(mailboxIndexes, index => index["name"] == "ux_mailbox_normalized");
            Assert.Contains(mailboxIndexes, index => index["name"] == "ux_mailbox_key_v2");
        }
        finally
        {
            await database.DropCollectionAsync(domainCollection);
            await database.DropCollectionAsync(mailboxCollection);
        }
    }

    private static MongoValidationIntelligenceStore Store(
        IMongoClient client,
        IOptions<EmailValidationOptions> options) => new(
            client,
            options,
            new ValidationPersistenceMetrics(),
            NullLogger<MongoValidationIntelligenceStore>.Instance);

    private static DomainIntelligence Domain() => new()
    {
        Domain = "example.test",
        DomainExists = true,
        Dns = new DnsLookupResult(
            DnsStatus.Success, true, [new MxRecord(10, "mx.example.test")], false, TimeSpan.Zero),
        Provider = new ProviderDetectionResult(
            MailProvider.GenericSmtp, 0.8, TopologyFingerprint: "topology-1"),
        CatchAll = new CatchAllDetectionResult(CatchAllStatus.NotCatchAll, 1, 0, 1, 0, Confidence: 0.9),
        ObservedAt = DateTimeOffset.UtcNow,
        EvidenceExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        StrategyVersion = "1.1.0"
    };

    private static ValidationObservation Observation() => new(
        "example.test",
        ValidationObservationType.MailboxProbe,
        MailProvider.GenericSmtp,
        "mx.example.test",
        CatchAllStatus.NotCatchAll,
        0.9,
        SmtpResponseCategory.Accepted,
        DateTimeOffset.UtcNow,
        10,
        TopologyFingerprint: "topology-1");

    private static MailboxIntelligence Mailbox(string email = "person@example.test")
    {
        var result = new EmailValidationResult
        {
            Email = email,
            NormalizedEmail = email,
            MailboxKey = MailboxIdentity.Create(email).Key,
            Status = EmailValidationStatus.LikelyValid,
            Confidence = 0.9,
            ConfidenceType = ConfidenceType.Heuristic,
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
            DetailedStatus = DetailedStatus.MailboxAccepted,
            DetailedStatuses = [DetailedStatus.MailboxAccepted],
            Metadata = new ValidationResultMetadata(
                new ValidationPolicyVersions("1.1.0", "2.2.0", "3.1.0", "1.1.0"),
                DateTimeOffset.UtcNow,
                MxTopologyFingerprint: "topology-1")
        };
        return new MailboxIntelligence
        {
            NormalizedEmail = result.NormalizedEmail,
            MailboxKey = result.MailboxKey,
            PreviousStatus = result.Status,
            PreviousMailboxResult = result.Checks.Mailbox,
            PreviousConfidence = result.Confidence,
            PreviousConfidenceType = result.ConfidenceType,
            LastValidatedAt = result.Metadata.ValidatedAt,
            LastStrongPositiveEvidenceAt = result.Metadata.ValidatedAt,
            ProviderAtValidation = result.MailProvider,
            Policy = result.Metadata.Policy,
            MxTopologyFingerprint = result.Metadata.MxTopologyFingerprint,
            UsedLiveSmtp = true,
            LastResult = result
        };
    }
}
