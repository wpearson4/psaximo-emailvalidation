using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace EmailValidation.IntegrationTests;

public sealed class MongoValidationLifecycleStoreTests
{
    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task LifecycleStore_UsesCompareAndSetAndDurableOutbox()
    {
        var connectionString = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var databaseName = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO_DATABASE")
            ?? "email-validation-integration-tests";
        var collectionName = $"EmailValidationLifecycle_{Guid.NewGuid():N}";
        var options = Options.Create(new EmailValidationOptions
        {
            Persistence = new PersistenceOptions
            {
                Enabled = true,
                Provider = "MongoDB",
                ConnectionString = connectionString,
                DatabaseName = databaseName,
                LifecycleCollection = collectionName
            }
        });
        var client = new MongoClient(connectionString);
        var database = client.GetDatabase(databaseName);
        var store = new MongoValidationLifecycleStore(
            client, options, TimeProvider.System, NullLogger<MongoValidationLifecycleStore>.Instance);

        try
        {
            await store.InitializeAsync();
            await store.InitializeAsync();
            var indexes = await (await database.GetCollection<object>(collectionName).Indexes.ListAsync()).ToListAsync();
            var initial = Lifecycle();
            var inserted = await store.TrySaveAsync(initial, 0);
            var claimed = await store.TryClaimAsync(initial.ValidationId, TimeSpan.FromMinutes(1));

            Assert.True(inserted.Applied);
            Assert.Contains(indexes, index => index["name"] == "ux_lifecycle_active_email");
            Assert.NotNull(claimed);
            Assert.Equal($"{initial.ValidationId}:2", claimed.Message.MessageId);

            var marked = await store.MarkScheduledAsync(initial.ValidationId, claimed.Message.MessageId,
                new(true, claimed.Message.MessageId, claimed.ScheduledAt));

            var current = await store.GetAsync(initial.ValidationId);
            Assert.True(current!.RetryScheduled);
            Assert.Equal(ValidationLifecycleState.RetryWaiting, current.LifecycleState);
            Assert.Equal(4, current.Sequence);
            var final = current! with
            {
                ResultState = ValidationResultState.Final,
                AttemptNumber = 2,
                PendingRevalidation = null,
                Version = current.Version + 1,
                CurrentResult = current.CurrentResult with
                {
                    Status = EmailValidationStatus.Valid,
                    ResultState = ValidationResultState.Final,
                    AttemptNumber = 2
                }
            };
            var finalized = await store.TrySaveAsync(final, current.Version);
            var stale = await store.TrySaveAsync(initial with { Version = 2 }, 1);
            var persisted = await store.GetAsync(initial.ValidationId);

            Assert.True(finalized.Applied);
            Assert.True(marked);
            Assert.False(stale.Applied);
            Assert.Equal(ValidationResultState.Final, persisted!.ResultState);
            Assert.Equal(EmailValidationStatus.Valid, persisted.CurrentResult.Status);
        }
        finally
        {
            await database.DropCollectionAsync(collectionName);
        }
    }

    private static ValidationLifecycle Lifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var result = new EmailValidationResult
        {
            Email = "person@example.test",
            NormalizedEmail = "person@example.test",
            Status = EmailValidationStatus.Unknown,
            Confidence = 0.3,
            Checks = new EmailValidationChecks
            {
                SyntaxValid = true,
                DomainExists = true,
                MxPresent = true,
                Mailbox = SmtpMailboxStatus.TemporaryFailure
            },
            MailProvider = MailProvider.Microsoft365,
            ReasonCodes = [ReasonCode.TemporaryFailure],
            ResultState = ValidationResultState.Provisional,
            ValidationId = id,
            AttemptNumber = 1,
            MaximumAttempts = 2,
            FirstValidatedAt = now,
            LastValidatedAt = now
        };
        var message = new EmailRevalidationMessageV1(
            id, 2, 2, now, now, now.AddMinutes(60), "Microsoft365",
            EmailValidationStatus.Unknown, DetailedStatus.Unknown, "2.2.0");
        return new()
        {
            ValidationId = id,
            NormalizedEmail = result.NormalizedEmail,
            Request = new(true),
            ResultState = ValidationResultState.Provisional,
            AttemptNumber = 1,
            MaximumAttempts = 2,
            CurrentResult = result,
            Attempts = [new(1, result.Status, result.SubStatus, result.Confidence, result.MailProvider,
                result.ReasonCodes, now, ValidationResultSource.LiveValidation, message.ScheduledRetryAt)],
            FirstValidatedAt = now,
            LastValidatedAt = now,
            NextRetryAt = message.ScheduledRetryAt,
            PendingRevalidation = new(message, now, message.ScheduledRetryAt),
            LifecycleState = ValidationLifecycleState.Provisional,
            Sequence = 3,
            Version = 1
        };
    }
}
