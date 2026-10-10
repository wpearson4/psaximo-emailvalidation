using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EmailValidation.IntegrationTests;

public sealed class MongoRetentionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task Retention_DryRunAndApply_ProtectActiveRetriesAndUseSeparateClocks()
    {
        var uri = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(uri)) return;
        var client = new MongoClient(uri);
        var config = new EmailValidationOptions { Persistence = new() { Provider = "MongoDB", DatabaseName = "ev-retention-" + Guid.NewGuid().ToString("N") } };
        var database = client.GetDatabase(config.Persistence.DatabaseName);
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var old = now.AddDays(-100);
        IMongoCollection<BsonDocument> Collection(string name) => database.GetCollection<BsonDocument>(name);
        try
        {
            var lifecycles = Collection(config.Persistence.LifecycleCollection);
            await lifecycles.InsertManyAsync([
                Lifecycle("expired", old.ToOffset(TimeSpan.FromHours(-5)), false), Lifecycle("pending", old, true), Lifecycle("fresh", now, false),
                Lifecycle("active-job-final-item", old, false, "active-job"), Lifecycle("stale-projection", old, true, "stale-job")]);
            var jobs = Collection(config.Jobs.JobCollection);
            await jobs.InsertManyAsync([Job("old-job", old, 0), Job("active-job", old, 1), Job("stale-job", old, 0)]);
            var items = Collection(config.Jobs.ItemCollection);
            await items.InsertManyAsync([Item("old-job", "expired"), Item("active-job", "active-job-final-item"), Item("stale-job", "stale-projection")]);
            var mailboxes = Collection(config.Persistence.MailboxCollection);
            await mailboxes.InsertManyAsync([
                new BsonDocument { { "_id", "expired-mailbox" }, { "MailboxKey", "unreferenced" }, { "LastValidatedAt", old.UtcDateTime } },
                new BsonDocument { { "_id", "active-mailbox" }, { "MailboxKey", "pending" }, { "LastValidatedAt", old.UtcDateTime } }]);
            var snapshots = Collection(config.Persistence.FeatureSnapshotCollection);
            await snapshots.InsertManyAsync([
                new BsonDocument { { "_id", "benchmark-100-days" }, { "SnapshotAtUtc", old.UtcDateTime } },
                new BsonDocument { { "_id", "benchmark-400-days" }, { "SnapshotAtUtc", now.AddDays(-400).UtcDateTime } }]);
            var store = new MongoValidationRetentionStore(client, Options.Create(config));
            var request = new ValidationRetentionRequest(now.AddDays(-90), now.AddDays(-365));
            var dry = await store.SweepAsync(request);
            Assert.True(dry.DryRun);
            Assert.Equal(1, dry.Records["lifecycles"]);
            Assert.Equal(5, await lifecycles.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            var applied = await store.SweepAsync(request with { DryRun = false });
            Assert.Equal(1, applied.Records["lifecycles"]);
            Assert.Equal(1, applied.Records["jobs"]);
            Assert.Equal(1, applied.Records["jobItems"]);
            Assert.Equal(1, applied.Records["mailboxes"]);
            Assert.Equal(1, applied.Records["snapshots"]);
            Assert.True(await lifecycles.Find(new BsonDocument("_id", "pending")).AnyAsync());
            Assert.True(await jobs.Find(new BsonDocument("_id", "stale-job")).AnyAsync());
            Assert.True(await snapshots.Find(new BsonDocument("_id", "benchmark-100-days")).AnyAsync());
            var repeated = await store.SweepAsync(request with { DryRun = false });
            Assert.All(repeated.Records.Values, value => Assert.Equal(0, value));
            Assert.DoesNotContain("@", JsonSerializer.Serialize(applied), StringComparison.Ordinal);
        }
        finally { await client.DropDatabaseAsync(config.Persistence.DatabaseName); }
    }

    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task RetentionClaim_FencesFailedJobRestart_AndResumesInterruptedCleanup()
    {
        var uri = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(uri)) return;
        var client = new MongoClient(uri);
        var config = new EmailValidationOptions { Persistence = new() { Provider = "MongoDB",
            DatabaseName = "ev-retention-claim-" + Guid.NewGuid().ToString("N") } };
        var database = client.GetDatabase(config.Persistence.DatabaseName);
        var now = DateTimeOffset.UtcNow;
        try
        {
            var jobs = database.GetCollection<BsonDocument>(config.Jobs.JobCollection);
            var claimed = Job("claimed", now.AddDays(-100), 0);
            claimed["State"] = (int)ValidationJobState.Failed;
            claimed["RetentionDeleting"] = true;
            var resumable = Job("resumable", now.AddDays(-100), 0);
            resumable["State"] = (int)ValidationJobState.Failed;
            await jobs.InsertManyAsync([claimed, resumable]);
            var items = database.GetCollection<BsonDocument>(config.Jobs.ItemCollection);
            await items.InsertManyAsync([Item("claimed", "old-final"), Item("resumable", "old-final")]);
            var jobStore = new MongoValidationJobStore(client, Options.Create(config), TimeProvider.System);
            await Assert.ThrowsAsync<InvalidOperationException>(() => jobStore.QueueDispatchAsync("claimed", "dispatch", 1));
            // A legacy job without the new field can still win the restart race.
            await jobStore.QueueDispatchAsync("resumable", "dispatch", 1);
            var retention = new MongoValidationRetentionStore(client, Options.Create(config));
            var report = await retention.SweepAsync(new(now.AddDays(-90), now.AddDays(-365), DryRun: false));
            Assert.Equal(1, report.Records["jobs"]);
            Assert.False(await jobs.Find(new BsonDocument("_id", "claimed")).AnyAsync());
            Assert.True(await jobs.Find(new BsonDocument("_id", "resumable")).AnyAsync());
            Assert.True(await items.Find(new BsonDocument("JobId", "resumable")).AnyAsync());
            Assert.False(await items.Find(new BsonDocument("JobId", "claimed")).AnyAsync());
        }
        finally { await client.DropDatabaseAsync(config.Persistence.DatabaseName); }
    }

    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task SharedSuppressions_AreFreshAcrossHostsAndTenantScoped()
    {
        var uri = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(uri)) return;
        var client = new MongoClient(uri);
        var directory = Path.Combine(Path.GetTempPath(), "ev-suppression-" + Guid.NewGuid().ToString("N"));
        var config = Options.Create(new EmailValidationOptions { Persistence = new() { Provider = "MongoDB",
            DatabaseName = "ev-suppression-" + Guid.NewGuid().ToString("N"), StoragePath = directory } });
        try
        {
            var legacy = new JsonValidationIntelligenceStore(config);
            var first = new MongoSuppressionStore(client, config, legacy);
            var second = new MongoSuppressionStore(client, config, legacy);
            Assert.Null(await second.GetScopedAsync("Person@example.test", "tenant-a"));
            await first.AddAsync(new("Person@example.test", "CustomerPolicy", "ApprovedImport", DateTimeOffset.UtcNow) { TenantId = "tenant-a" });
            Assert.NotNull(await second.GetScopedAsync("Person@example.test", "tenant-a"));
            Assert.Null(await second.GetScopedAsync("Person@example.test", "tenant-b"));
            Assert.Null(await second.GetScopedAsync("person@example.test", "tenant-a"));
            await legacy.AddAsync(new("legacy@example.test", "HistoricalHardBounce", "Legacy", DateTimeOffset.UtcNow));
            Assert.NotNull(await second.GetScopedAsync("legacy@example.test", "tenant-b"));
            Assert.Equal(2, await client.GetDatabase(config.Value.Persistence.DatabaseName)
                .GetCollection<BsonDocument>(config.Value.Persistence.SuppressionCollection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            await client.DropDatabaseAsync(config.Value.Persistence.DatabaseName);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static BsonDocument Lifecycle(string id, DateTimeOffset at, bool active, string? job = null)
    {
        var model = new ValidationLifecycle { ValidationId = id, NormalizedEmail = "synthetic@example.test",
            MailboxKey = id, Request = new(TenantId: "tenant-a", JobId: job), ResultState = active ? ValidationResultState.Provisional : ValidationResultState.Final,
            AttemptNumber = 1, MaximumAttempts = 2, CurrentResult = new() { Email = "synthetic@example.test", Checks = new(), Status = EmailValidationStatus.Unknown },
            LifecycleState = active ? ValidationLifecycleState.RetryWaiting : ValidationLifecycleState.Final, Version = 1 };
        return new BsonDocument { { "_id", id }, { "MailboxKey", id }, { "ResultState", (int)model.ResultState },
            { "LifecycleState", (int)model.LifecycleState }, { "PendingMessageId", active ? "pending" : BsonNull.Value },
            { "ExecutionOwner", BsonNull.Value }, { "UpdatedAt", Offset(at) }, { "Version", 1L },
            { "PayloadJson", JsonSerializer.Serialize(model, Json) } };
    }
    private static BsonDocument Job(string id, DateTimeOffset at, int pending) => new() {
        { "_id", id }, { "State", 3 }, { "ProvisionalItems", pending }, { "UpdatedAtUtc", Offset(at) } };
    private static BsonDocument Item(string job, string validation) => new() {
        { "_id", job + ":0" }, { "JobId", job }, { "ValidationId", validation }, { "State", 2 }, { "ResultState", 0 } };
    private static BsonArray Offset(DateTimeOffset at) => [at.Ticks, (int)at.Offset.TotalMinutes];
}
