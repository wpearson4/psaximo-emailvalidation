using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Bson;

namespace EmailValidation.IntegrationTests;

public sealed class MongoProjectionOutboxTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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

            var competing = await Task.WhenAll(
                store.ClaimAsync(1, "worker-a", TimeSpan.FromMinutes(1)),
                store.ClaimAsync(1, "worker-b", TimeSpan.FromMinutes(1)));
            Assert.Single(competing.SelectMany(claims => claims));
            Assert.Single(competing, claims => claims.Count == 0);
            var claimedDocument = await database.GetCollection<BsonDocument>(collectionName)
                .Find(Builders<BsonDocument>.Filter.Eq("_id", "event-1")).SingleAsync();
            Assert.Equal(BsonType.DateTime, claimedDocument["NextPublishAttemptAtUtc"].BsonType);
            Assert.Equal(BsonType.DateTime, claimedDocument["LockExpiresAtUtc"].BsonType);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "MongoIntegration")]
    public async Task LegacyTimestamps_MigrateIdempotentlyWithoutLosingEventsOrLeases(bool existingClaimIndex)
    {
        var connection = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var databaseName = "ev_outbox_dates_" + Guid.NewGuid().ToString("N");
        var settings = new EmailValidationOptions
        {
            Persistence = new() { DatabaseName = databaseName },
            Projection = new() { Enabled = true }
        };
        var client = new MongoClient(connection);
        var raw = client.GetDatabase(databaseName).GetCollection<BsonDocument>(settings.Projection.Outbox.CollectionName);
        var now = new DateTimeOffset(2026, 10, 10, 7, 0, 0, TimeSpan.FromHours(-5));
        var time = new AdjustableTimeProvider(now);
        MongoProjectionOutbox Store() => new(client, Options.Create(settings), time, NullLogger<MongoProjectionOutbox>.Instance);
        BsonArray Legacy(DateTimeOffset at) => new() { at.Ticks, (int)at.Offset.TotalMinutes };
        BsonDocument Document(string id, ProjectionOutboxState state, DateTimeOffset due, DateTimeOffset? lease = null)
        {
            var envelope = Event() with { EventId = id };
            return new BsonDocument
            {
                ["_id"] = id, ["EventType"] = envelope.EventType, ["SchemaVersion"] = envelope.SchemaVersion,
                ["PayloadJson"] = JsonSerializer.Serialize(envelope, JsonOptions),
                ["OccurredAtUtc"] = Legacy(now.AddMinutes(-10)), ["CreatedAtUtc"] = Legacy(now.AddMinutes(-10)),
                ["State"] = (int)state, ["PublishAttemptCount"] = lease is null ? 0 : 1,
                // Existing indexes cannot contain two arrays in a single document.
                ["NextPublishAttemptAtUtc"] = existingClaimIndex && lease is not null ? new BsonDateTime(due.UtcDateTime) : Legacy(due),
                ["LockExpiresAtUtc"] = lease is { } until ? Legacy(until) : BsonNull.Value,
                ["LockedBy"] = lease is null ? BsonNull.Value : new BsonString("original-owner"),
                ["PublishedAtUtc"] = BsonNull.Value
            };
        }
        var documents = new[]
        {
            Document("due", ProjectionOutboxState.Pending, now.AddMinutes(-1)),
            Document("future", ProjectionOutboxState.Pending, now.AddMinutes(5)),
            Document("expired", ProjectionOutboxState.Publishing, now.AddMinutes(-5), now.AddMinutes(-1)),
            Document("active", ProjectionOutboxState.Publishing, now.AddMinutes(-5), now.AddMinutes(10))
        };
        try
        {
            await raw.InsertManyAsync(documents);
            if (existingClaimIndex)
                await raw.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("State").Ascending("NextPublishAttemptAtUtc").Ascending("LockExpiresAtUtc"),
                    new CreateIndexOptions { Name = "ix_projection_outbox_claim" }));
            var first = Store();
            var second = Store();
            await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
            await first.InitializeAsync();
            var migrated = await raw.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            Assert.Equal(documents.Length, migrated.Count);
            foreach (var saved in migrated)
            {
                var original = documents.Single(document => document["_id"] == saved["_id"]);
                foreach (var field in new[] { "PayloadJson", "State", "PublishAttemptCount", "LockedBy" })
                    Assert.Equal(original[field], saved[field]);
                foreach (var field in new[] { "OccurredAtUtc", "CreatedAtUtc", "NextPublishAttemptAtUtc" })
                    Assert.Equal(BsonType.DateTime, saved[field].BsonType);
                if (!saved["LockExpiresAtUtc"].IsBsonNull)
                    Assert.Equal(BsonType.DateTime, saved["LockExpiresAtUtc"].BsonType);
            }
            Assert.Equal(now.AddMinutes(-10), (await first.GetBacklogAsync()).OldestCreatedAtUtc);
            var claims = await Task.WhenAll(first.ClaimAsync(4, "a", TimeSpan.FromMinutes(1)),
                second.ClaimAsync(4, "b", TimeSpan.FromMinutes(1)));
            var claimed = claims.SelectMany(items => items).ToArray();
            Assert.Equal(["due", "expired"], claimed.Select(item => item.Event.EventId).Order().ToArray());
            foreach (var claim in claimed)
                await first.MarkPublishedAsync(claim.Event.EventId, claim.LockedBy!);
            Assert.Empty(await first.ClaimAsync(4, "c", TimeSpan.FromMinutes(1)));
            time.Advance(TimeSpan.FromMinutes(6));
            Assert.Equal("future", Assert.Single(await first.ClaimAsync(4, "c", TimeSpan.FromMinutes(1))).Event.EventId);
            var active = await raw.Find(Builders<BsonDocument>.Filter.Eq("_id", "active")).SingleAsync();
            Assert.Equal("original-owner", active["LockedBy"].AsString);
            Assert.Equal(now.AddMinutes(10).UtcDateTime, active["LockExpiresAtUtc"].ToUniversalTime());
        }
        finally { await client.DropDatabaseAsync(databaseName); }
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
            MailboxKey = MailboxIdentity.Create("person@example.test").Key,
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
            MailboxKey = result.MailboxKey,
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
