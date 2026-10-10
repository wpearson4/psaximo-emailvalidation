using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

/// <summary>
/// Bounded, restartable deletion of terminal records. Parents are removed last;
/// active work is never selected and terminal jobs are claimed before child deletion.
/// No TTL index can remove lifecycle ownership underneath a running worker.
/// </summary>
public sealed class MongoValidationRetentionStore(IMongoClient client, IOptions<EmailValidationOptions> options)
    : IValidationRetentionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BsonValue> _cursors = new(StringComparer.Ordinal);
    private readonly EmailValidationOptions _options = options.Value;
    private IMongoDatabase Database => client.GetDatabase(_options.Persistence.DatabaseName);
    private IMongoCollection<BsonDocument> Collection(string name) => Database.GetCollection<BsonDocument>(name);

    public async Task<ValidationRetentionReport> SweepAsync(ValidationRetentionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(request.BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.BatchSize, 1000);
        if (request.BenchmarkCutoffUtc > request.DetailCutoffUtc)
            throw new ArgumentException("Benchmark retention cannot be shorter than detail retention.", nameof(request));
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long protectedCount = 0;
        var persistence = _options.Persistence;
        var lifecycle = Collection(persistence.LifecycleCollection);
        var items = Collection(_options.Jobs.ItemCollection);
        var jobs = Collection(_options.Jobs.JobCollection);

        var terminal = new BsonDocument { { "ResultState", (int)ValidationResultState.Final },
            { "LifecycleState", (int)ValidationLifecycleState.Final }, { "PendingMessageId", BsonNull.Value },
            { "ExecutionOwner", BsonNull.Value } };
        var expiredLifecycles = await ReadBatchAsync(lifecycle, And(terminal, Older("UpdatedAt", request.DetailCutoffUtc)), "lifecycles").ConfigureAwait(false);
        var jobIds = expiredLifecycles.Select(doc => JsonSerializer.Deserialize<ValidationLifecycle>(doc["PayloadJson"].AsString, Json)?.Request.JobId)
            .Where(id => id is not null).Distinct().ToArray();
        var linkedJobs = await jobs.Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(jobIds))))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var protectedJobs = linkedJobs.Where(doc => !JobFinal(doc) || !IsOlder(doc, "UpdatedAtUtc", request.DetailCutoffUtc))
            .Select(doc => doc["_id"].AsString).ToHashSet(StringComparer.Ordinal);
        protectedJobs.UnionWith(await ActiveCanonicalJobsAsync(new BsonArray(jobIds)).ConfigureAwait(false));
        var deletable = new List<BsonDocument>();
        foreach (var document in expiredLifecycles)
        {
            var model = JsonSerializer.Deserialize<ValidationLifecycle>(document["PayloadJson"].AsString, Json);
            if (model is null || model.PendingRevalidation is not null || model.RetryScheduled || model.ExecutionLease is not null ||
                model.Request.JobId is { } jobId && protectedJobs.Contains(jobId)) { protectedCount++; continue; }
            deletable.Add(document);
        }
        await DeleteInspectedAsync(lifecycle, deletable, "Version", "lifecycles").ConfigureAwait(false);
        // A completed job may still contain provisional retries. Verify child state in
        // one indexed query per bounded job batch before removing its result copies.
        var expiredJobs = await ReadBatchAsync(jobs, And(new BsonDocument("State", new BsonDocument("$in", new BsonArray { 3, 4, 5 })),
                new BsonDocument("ProvisionalItems", 0), Older("UpdatedAtUtc", request.DetailCutoffUtc)), "jobs").ConfigureAwait(false);
        var candidates = expiredJobs.Select(doc => doc["_id"]).ToArray();
        var activeItemFilter = And(new BsonDocument("JobId", new BsonDocument("$in", new BsonArray(candidates))),
            new BsonDocument("$or", new BsonArray {
                new BsonDocument("State", new BsonDocument("$in", new BsonArray { 0, 1 })),
                new BsonDocument("ResultState", (int)ValidationResultState.Provisional),
                new BsonDocument("LeaseOwner", new BsonDocument("$type", "string")) }));
        using var activeIds = await items.DistinctAsync<string>("JobId", activeItemFilter, cancellationToken: cancellationToken).ConfigureAwait(false);
        var activeJobIds = (await activeIds.ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        // Also protect stale item projections whose canonical lifecycle still owns work.
        activeJobIds.UnionWith(await ActiveCanonicalJobsAsync(new BsonArray(candidates)).ConfigureAwait(false));
        var removableJobs = expiredJobs.Where(doc => !activeJobIds.Contains(doc["_id"].AsString)).ToArray();
        protectedCount += expiredJobs.Count - removableJobs.Length;
        if (!request.DryRun && removableJobs.Length > 0)
        {
            // Competes atomically with failed-job restart. A persisted claim also
            // makes interrupted cleanup resumable without reopening the job.
            var claims = removableJobs.Select(doc => (WriteModel<BsonDocument>)new UpdateOneModel<BsonDocument>(
                new BsonDocument { { "_id", doc["_id"] }, { "UpdatedAtUtc", doc["UpdatedAtUtc"] }, { "State", doc["State"] } },
                new BsonDocument("$set", new BsonDocument("RetentionDeleting", true)))).ToArray();
            await jobs.BulkWriteAsync(claims, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
            var claimed = await jobs.Find(And(new BsonDocument("_id", new BsonDocument("$in",
                    new BsonArray(removableJobs.Select(doc => doc["_id"])))), new BsonDocument("RetentionDeleting", true)))
                .Project(new BsonDocument("_id", 1)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var claimedIds = claimed.Select(doc => doc["_id"]).ToHashSet();
            protectedCount += removableJobs.Count(doc => !claimedIds.Contains(doc["_id"]));
            removableJobs = removableJobs.Where(doc => claimedIds.Contains(doc["_id"])).ToArray();
        }
        var jobFilter = new BsonDocument("JobId", new BsonDocument("$in", new BsonArray(removableJobs.Select(doc => doc["_id"]))));
        counts["jobItems"] = request.DryRun
            ? await items.CountDocumentsAsync(jobFilter, cancellationToken: cancellationToken).ConfigureAwait(false)
            : (await items.DeleteManyAsync(jobFilter, cancellationToken).ConfigureAwait(false)).DeletedCount;
        await DeleteInspectedAsync(jobs, removableJobs, "UpdatedAtUtc", "jobs").ConfigureAwait(false);
        await DeleteOldEvidenceAsync(persistence.MailboxCollection, "LastValidatedAt", request.DetailCutoffUtc, "mailboxes", true).ConfigureAwait(false);
        await DeleteOldEvidenceAsync(persistence.DomainCollection, "LastObservedAt", request.DetailCutoffUtc, "domains", false).ConfigureAwait(false);
        await DeleteOldEvidenceAsync(persistence.FeatureSnapshotCollection, "SnapshotAtUtc", request.BenchmarkCutoffUtc, "snapshots", false).ConfigureAwait(false);
        await DeleteOldEvidenceAsync(persistence.OutcomeObservationCollection, "ObservedAtUtc", request.BenchmarkCutoffUtc, "outcomes", false).ConfigureAwait(false);
        // Projection envelopes are pseudonymous; their own shorter published TTL still applies.
        await DeleteOldEvidenceAsync(_options.Projection.Outbox.CollectionName, "OccurredAtUtc", request.BenchmarkCutoffUtc, "projectionOutbox", false).ConfigureAwait(false);
        // Reconcile ownership/idempotency copies only after their authoritative parent
        // is absent. This is retryable even if a previous cleanup stopped mid-batch.
        var commercial = Collection(persistence.CommercialResourceCollection);
        var oldAccess = await ReadBatchAsync(commercial, Older("CreatedAtUtc", request.DetailCutoffUtc), "access").ConfigureAwait(false);
        var resources = oldAccess.Select(doc => doc.GetValue("ResourceId", BsonNull.Value)).ToArray();
        var resourceFilter = new BsonDocument("_id", new BsonDocument("$in", new BsonArray(resources)));
        var liveJobs = await jobs.Find(resourceFilter).Project(new BsonDocument("_id", 1)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var liveValidations = await lifecycle.Find(resourceFilter).Project(new BsonDocument("_id", 1)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var live = liveJobs.Concat(liveValidations).Select(doc => doc["_id"]).ToHashSet();
        await DeleteInspectedAsync(commercial, oldAccess.Where(doc => !live.Contains(doc.GetValue("ResourceId", BsonNull.Value))).ToArray(),
            "CreatedAtUtc", "accessRecords").ConfigureAwait(false);
        return new(request.DryRun, counts, protectedCount);

        async Task<List<BsonDocument>> ReadBatchAsync(IMongoCollection<BsonDocument> collection, BsonDocument filter, string label)
        {
            if (!request.DryRun && _cursors.TryGetValue(label, out var after))
                filter = And(filter, new BsonDocument("_id", new BsonDocument("$gt", after)));
            var batch = await collection.Find(filter).Sort(new BsonDocument("_id", 1))
                .Limit(request.BatchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (!request.DryRun)
            {
                if (batch.Count == 0) _cursors.TryRemove(label, out _);
                else _cursors[label] = batch[^1]["_id"];
            }
            return batch;
        }

        async Task<HashSet<string>> ActiveCanonicalJobsAsync(BsonArray ids)
        {
            if (ids.Count == 0) return new(StringComparer.Ordinal);
            var pipeline = new BsonDocument[] {
                new("$match", new BsonDocument("JobId", new BsonDocument("$in", ids))),
                new("$lookup", new BsonDocument { { "from", persistence.LifecycleCollection },
                    { "localField", "ValidationId" }, { "foreignField", "_id" }, { "as", "lifecycle" } }),
                new("$unwind", "$lifecycle"),
                new("$match", new BsonDocument("$or", new BsonArray {
                    new BsonDocument("lifecycle.ResultState", (int)ValidationResultState.Provisional),
                    new BsonDocument("lifecycle.ExecutionOwner", new BsonDocument("$type", "string")),
                    new BsonDocument("lifecycle.PendingMessageId", new BsonDocument("$type", "string")),
                    new BsonDocument("$nor", new BsonArray { Older("lifecycle.UpdatedAt", request.DetailCutoffUtc) }) })),
                new("$group", new BsonDocument("_id", "$JobId")) };
            var active = await items.Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return active.Select(doc => doc["_id"].AsString).ToHashSet(StringComparer.Ordinal);
        }

        async Task DeleteOldEvidenceAsync(string name, string clock, DateTimeOffset cutoff, string label, bool protectMailbox)
        {
            var collection = Collection(name);
            var documents = await ReadBatchAsync(collection, Older(clock, cutoff), label).ConfigureAwait(false);
            if (protectMailbox && documents.Count > 0)
            {
                using var active = await lifecycle.DistinctAsync<BsonValue>("MailboxKey",
                    And(new BsonDocument("MailboxKey", new BsonDocument("$in",
                        new BsonArray(documents.Select(doc => doc.GetValue("MailboxKey", BsonNull.Value))))),
                    new BsonDocument("ResultState", (int)ValidationResultState.Provisional)),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var protectedKeys = (await active.ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
                protectedCount += documents.RemoveAll(doc => protectedKeys.Contains(doc.GetValue("MailboxKey", BsonNull.Value)));
            }
            await DeleteInspectedAsync(collection, documents, documents.All(doc => doc.Contains("UpdatedAt")) ? "UpdatedAt" : clock, label).ConfigureAwait(false);
        }

        async Task DeleteInspectedAsync(IMongoCollection<BsonDocument> collection, IReadOnlyList<BsonDocument> documents, string version, string label)
        {
            if (request.DryRun) { counts[label] = documents.Count; return; }
            if (documents.Count == 0) { counts[label] = 0; return; }
            var deletes = documents.Select(doc => (WriteModel<BsonDocument>)new DeleteOneModel<BsonDocument>(
                new BsonDocument { { "_id", doc["_id"] }, { version, doc.GetValue(version, BsonNull.Value) } })).ToArray();
            counts[label] = (await collection.BulkWriteAsync(deletes,
                new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false)).DeletedCount;
        }
    }

    private static bool JobFinal(BsonDocument doc) => doc.GetValue("State", -1).AsInt32 is 3 or 4 or 5 &&
        doc.GetValue("ProvisionalItems", 1).AsInt32 == 0;

    private static bool IsOlder(BsonDocument doc, string field, DateTimeOffset cutoff)
    {
        var value = doc.GetValue(field, BsonNull.Value);
        return value.IsValidDateTime ? value.ToUniversalTime() < cutoff.UtcDateTime :
            value.IsBsonArray && value.AsBsonArray.Count == 2 &&
            new DateTimeOffset(value[0].ToInt64(), TimeSpan.FromMinutes(value[1].ToInt32())) < cutoff;
    }

    // Supports legacy DateTimeOffset tick/offset arrays as well as scalar UTC dates.
    internal static BsonDocument Older(string field, DateTimeOffset cutoff) => new("$or", new BsonArray {
        new BsonDocument(field, new BsonDocument { { "$type", "date" }, { "$lt", cutoff.UtcDateTime } }),
        new BsonDocument { { field + ".0", new BsonDocument("$lt", cutoff.UtcTicks) }, { field + ".1", 0 } },
        new BsonDocument { { field + ".1", new BsonDocument { { "$type", "number" }, { "$ne", 0 } } },
            { "$expr", new BsonDocument("$lt", new BsonArray {
                new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", "$" + field),
                    new BsonDocument("$subtract", new BsonArray {
                        new BsonDocument("$arrayElemAt", new BsonArray { "$" + field, 0 }),
                        new BsonDocument("$multiply", new BsonArray {
                            new BsonDocument("$arrayElemAt", new BsonArray { "$" + field, 1 }), 600_000_000L }) }), cutoff.UtcTicks }), cutoff.UtcTicks }) } }
    });
    private static BsonDocument And(params BsonDocument[] filters) => new("$and", new BsonArray(filters));
}
