using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace EmailValidation.IntegrationTests;

public sealed class MongoProjectionOutboxTests
{
    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task Outbox_IsIdempotentAtomicallyClaimedReclaimableAndTtlSafe()
    {
        var connectionString = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var databaseName = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO_DATABASE")
            ?? "email-validation-integration-tests";
        var collectionName = $"EmailValidationProjectionOutbox_{Guid.NewGuid():N}";
        var configured = new EmailValidationOptions
        {
            Persistence = new PersistenceOptions
            {
                Enabled = true,
                Provider = "MongoDB",
                ConnectionString = connectionString,
                DatabaseName = databaseName
            },
            Projection = new EmailValidationProjectionOptions
            {
                Enabled = true,
                Outbox = new ProjectionOutboxOptions
                {
                    CollectionName = collectionName,
                    PublishedRetentionDays = 2,
                    MaximumPublishAttempts = 10
                }
            }
        };
        var client = new MongoClient(connectionString);
        var database = client.GetDatabase(databaseName);
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var store = new MongoProjectionOutbox(client, Options.Create(configured), time,
            NullLogger<MongoProjectionOutbox>.Instance);
        try
        {
            await store.InitializeAsync();
            Assert.True(await store.EnqueueAsync(Event()));
            Assert.False(await store.EnqueueAsync(Event()));

            var first = await store.ClaimAsync(1, "worker-a", TimeSpan.FromMinutes(1));
            var concurrent = await store.ClaimAsync(1, "worker-b", TimeSpan.FromMinutes(1));
            Assert.Single(first);
            Assert.Empty(concurrent);

            time.Advance(TimeSpan.FromMinutes(2));
            var reclaimed = await store.ClaimAsync(1, "worker-b", TimeSpan.FromMinutes(1));
            Assert.Single(reclaimed);
            await store.ReleaseAsync(reclaimed[0].Event.EventId, "worker-b", time.GetUtcNow().AddMinutes(1),
                "transient", false);
            Assert.Empty(await store.ClaimAsync(1, "worker-c", TimeSpan.FromMinutes(1)));

            time.Advance(TimeSpan.FromMinutes(2));
            var finalClaim = await store.ClaimAsync(1, "worker-c", TimeSpan.FromMinutes(1));
            await store.MarkPublishedAsync(finalClaim[0].Event.EventId, "worker-c");
            Assert.Equal(0, (await store.GetBacklogAsync()).PendingCount);

            var indexes = await (await database.GetCollection<object>(collectionName).Indexes.ListAsync()).ToListAsync();
            var ttl = indexes.Single(index => index["name"] == "ttl_projection_outbox_published");
            Assert.Equal(172800, ttl["expireAfterSeconds"].ToInt64());
        }
        finally
        {
            await database.DropCollectionAsync(collectionName);
        }
    }

    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task Backfill_IsDryRunBoundedIdempotentAndDoesNotMutateCanonicalLifecycle()
    {
        var connectionString = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var databaseName = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO_DATABASE")
            ?? "email-validation-integration-tests";
        var suffix = Guid.NewGuid().ToString("N");
        var lifecycleCollection = $"EmailValidationLifecycle_{suffix}";
        var outboxCollection = $"EmailValidationProjectionOutbox_{suffix}";
        var checkpointCollection = $"EmailValidationProjectionCheckpoints_{suffix}";
        var configured = new EmailValidationOptions
        {
            Persistence = new PersistenceOptions
            {
                Enabled = true,
                Provider = "MongoDB",
                ConnectionString = connectionString,
                DatabaseName = databaseName,
                LifecycleCollection = lifecycleCollection
            },
            Projection = new EmailValidationProjectionOptions
            {
                Enabled = true,
                Environment = "test",
                Outbox = new ProjectionOutboxOptions
                {
                    CollectionName = outboxCollection,
                    CheckpointCollectionName = checkpointCollection
                },
                Privacy = new ProjectionPrivacyOptions
                {
                    EmailHashKey = "0123456789abcdef0123456789abcdef",
                    EmailHashKeyVersion = "v1"
                }
            }
        };
        var options = Options.Create(configured);
        var client = new MongoClient(connectionString);
        var database = client.GetDatabase(databaseName);
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var lifecycleStore = new MongoValidationLifecycleStore(client, options, time,
            NullLogger<MongoValidationLifecycleStore>.Instance);
        var outbox = new MongoProjectionOutbox(client, options, time,
            NullLogger<MongoProjectionOutbox>.Instance);
        var hmac = new HmacEmailCorrelationService(options, NullLogger<HmacEmailCorrelationService>.Instance);
        var factory = new ObservationEventFactory(hmac, options, time);
        var reconciler = new MongoProjectionReconciler(client, options, factory, outbox, time);
        try
        {
            await lifecycleStore.InitializeAsync();
            await outbox.InitializeAsync();
            await reconciler.InitializeAsync();
            var lifecycle = Lifecycle(time.GetUtcNow());
            Assert.True((await lifecycleStore.TrySaveAsync(lifecycle, 0)).Applied);
            var request = new ProjectionReplayRequest(
                time.GetUtcNow().AddMinutes(-1), time.GetUtcNow().AddMinutes(1),
                BatchSize: 1, MaximumEvents: 2, DryRun: true);

            var dryRun = await reconciler.BackfillAsync(request);
            var first = await reconciler.BackfillAsync(request with { DryRun = false });
            var second = await reconciler.BackfillAsync(request with { DryRun = false });
            var canonical = await lifecycleStore.GetAsync(lifecycle.ValidationId);

            Assert.Equal(2, dryRun.EventsConsidered);
            Assert.Equal(0, dryRun.EventsEnqueued);
            Assert.Equal(2, first.EventsEnqueued);
            Assert.Equal(0, second.EventsEnqueued);
            Assert.Equal(2, (await outbox.GetBacklogAsync()).PendingCount);
            Assert.Equal(lifecycle.Version, canonical!.Version);
            Assert.Equal(lifecycle.CurrentResult.Status, canonical.CurrentResult.Status);
        }
        finally
        {
            await database.DropCollectionAsync(lifecycleCollection);
            await database.DropCollectionAsync(outboxCollection);
            await database.DropCollectionAsync(checkpointCollection);
        }
    }

    private static EmailValidationObservationEnvelope Event()
    {
        var now = DateTimeOffset.UtcNow;
        return new("event-1", EmailValidationObservationTypes.AttemptV1, "v1", now, now, "test",
            null, null, "validation-1", null, 1,
            JsonSerializer.SerializeToElement(new { validationId = "validation-1", attemptNumber = 1 }));
    }

    private static ValidationLifecycle Lifecycle(DateTimeOffset now)
    {
        var id = Guid.NewGuid().ToString("N");
        var result = new EmailValidationResult
        {
            Email = "person@example.test",
            NormalizedEmail = "person@example.test",
            ValidationId = id,
            Status = EmailValidationStatus.Unknown,
            SubStatus = DetailedStatus.TemporaryFailure,
            Confidence = 0.4,
            Checks = new EmailValidationChecks { CatchAll = CatchAllStatus.Unknown },
            MailProvider = MailProvider.GenericSmtp,
            ResultState = ValidationResultState.Provisional,
            AttemptNumber = 1,
            MaximumAttempts = 2,
            DurationMs = 10
        };
        return new ValidationLifecycle
        {
            ValidationId = id,
            NormalizedEmail = result.NormalizedEmail,
            Request = new EmailValidationRequest(true, ValidationId: id, TenantId: "tenant-test"),
            ResultState = result.ResultState,
            AttemptNumber = 1,
            MaximumAttempts = 2,
            CurrentResult = result,
            Attempts = [new ValidationAttemptRecord(1, result.Status, result.SubStatus, result.Confidence,
                result.MailProvider, [], now, ValidationResultSource.LiveValidation, null,
                NormalizedRecipientDomain: "example.test")],
            FirstValidatedAt = now,
            LastValidatedAt = now,
            LastUpdatedAt = now,
            LifecycleState = ValidationLifecycleState.Provisional,
            Sequence = 1,
            Version = 1
        };
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
