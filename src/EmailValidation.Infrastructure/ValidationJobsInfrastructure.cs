using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public sealed class MongoValidationJobStore : IValidationJobStore, IValidationJobResultSink,
    IValidationJobDispatchOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IMongoCollection<JobDocument> _jobs;
    private readonly IMongoCollection<ItemDocument> _items;
    private readonly TimeProvider _timeProvider;

    public MongoValidationJobStore(IMongoClient client, IOptions<EmailValidationOptions> options, TimeProvider timeProvider)
    {
        var configuration = options.Value;
        var database = client.GetDatabase(configuration.Persistence.DatabaseName);
        _jobs = database.GetCollection<JobDocument>(configuration.Jobs.JobCollection);
        _items = database.GetCollection<ItemDocument>(configuration.Jobs.ItemCollection);
        _timeProvider = timeProvider;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _jobs.Indexes.CreateOneAsync(new CreateIndexModel<JobDocument>(
            Builders<JobDocument>.IndexKeys.Descending(value => value.CreatedAtUtc),
            new CreateIndexOptions { Name = "ix_job_created" }), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _jobs.Indexes.CreateOneAsync(new CreateIndexModel<JobDocument>(
            Builders<JobDocument>.IndexKeys.Ascending(value => value.SourceFileId)
                .Ascending(value => value.State)
                .Descending(value => value.CreatedAtUtc),
            new CreateIndexOptions { Name = "ix_job_source_file_state_created", Sparse = true }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _jobs.Indexes.CreateOneAsync(new CreateIndexModel<JobDocument>(
            Builders<JobDocument>.IndexKeys.Ascending(value => value.TenantId)
                .Ascending(value => value.SourceFileId)
                .Ascending(value => value.State)
                .Descending(value => value.CreatedAtUtc),
            new CreateIndexOptions { Name = "ix_job_tenant_source_file_state_created", Sparse = true }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _jobs.Indexes.CreateOneAsync(new CreateIndexModel<JobDocument>(
            Builders<JobDocument>.IndexKeys.Ascending(value => value.ItemsReady)
                .Ascending(value => value.DispatchState)
                .Ascending(value => value.DispatchRetryAtUtc)
                .Ascending(value => value.DispatchLeaseExpiresAtUtc)
                .Ascending(value => value.CreatedAtUtc),
            new CreateIndexOptions { Name = "ix_job_dispatch_outbox" }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _items.Indexes.CreateOneAsync(new CreateIndexModel<ItemDocument>(
            Builders<ItemDocument>.IndexKeys.Ascending(value => value.JobId).Ascending(value => value.Position),
            new CreateIndexOptions { Name = "ux_job_item_position", Unique = true }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _items.Indexes.CreateOneAsync(new CreateIndexModel<ItemDocument>(
            Builders<ItemDocument>.IndexKeys.Ascending(value => value.JobId).Ascending(value => value.State)
                .Ascending(value => value.LeaseExpiresAtUtc).Ascending(value => value.Position),
            new CreateIndexOptions { Name = "ix_job_item_claim" }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _items.Indexes.CreateOneAsync(new CreateIndexModel<ItemDocument>(
            Builders<ItemDocument>.IndexKeys.Ascending(value => value.JobId).Ascending(value => value.ValidationId),
            new CreateIndexOptions { Name = "ix_job_item_validation", Sparse = true }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateAsync(ValidationJobSnapshot job, IReadOnlyList<ValidationJobItem> items, CancellationToken cancellationToken = default)
    {
        await _jobs.InsertOneAsync(JobDocument.FromModel(job), cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await _items.InsertManyAsync(items.Select(ItemDocument.FromModel), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await _jobs.UpdateOneAsync(
                value => value.Id == job.JobId,
                Builders<JobDocument>.Update.Set(value => value.ItemsReady, true)
                    .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow()),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _items.DeleteManyAsync(value => value.JobId == job.JobId, CancellationToken.None).ConfigureAwait(false);
            await _jobs.DeleteOneAsync(value => value.Id == job.JobId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
        (await _jobs.Find(value => value.Id == jobId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false))?.ToModel();

    public async Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(
        string sourceFileId,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var successfulStates = new[]
        {
            ValidationJobState.Completed,
            ValidationJobState.CompletedWithErrors
        };
        var successfulFilter = Builders<JobDocument>.Filter.Eq(value => value.SourceFileId, sourceFileId)
            & Builders<JobDocument>.Filter.Eq(value => value.TenantId, tenantId)
            & Builders<JobDocument>.Filter.In(value => value.State, successfulStates);
        var successful = await _jobs.Find(successfulFilter)
            .SortByDescending(value => value.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var latest = successful ?? await _jobs.Find(value => value.SourceFileId == sourceFileId &&
                value.TenantId == tenantId)
            .SortByDescending(value => value.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return latest?.ToModel();
    }

    public async Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(string jobId, int skip, int take, CancellationToken cancellationToken = default) =>
        (await _items.Find(value => value.JobId == jobId).SortBy(value => value.Position).Skip(skip).Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Select(value => value.ToModel()).ToArray();

    public async Task<IReadOnlyList<ValidationJobItem>> ClaimPendingAsync(
        string jobId,
        int take,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        var claimed = new List<ValidationJobItem>(Math.Max(0, take));
        for (var index = 0; index < take; index++)
        {
            var now = _timeProvider.GetUtcNow();
            var available = Builders<ItemDocument>.Filter.Eq(value => value.JobId, jobId) &
                (Builders<ItemDocument>.Filter.Eq(value => value.State, ValidationJobItemState.Pending) |
                 (Builders<ItemDocument>.Filter.Eq(value => value.State, ValidationJobItemState.Processing) &
                  (Builders<ItemDocument>.Filter.Eq(value => value.LeaseExpiresAtUtc, null) |
                   Builders<ItemDocument>.Filter.Lte(value => value.LeaseExpiresAtUtc, now))));
            var document = await _items.FindOneAndUpdateAsync(
                available,
                Builders<ItemDocument>.Update
                    .Set(value => value.State, ValidationJobItemState.Processing)
                    .Set(value => value.LeaseOwner, leaseOwner)
                    .Set(value => value.LeaseExpiresAtUtc, now.Add(leaseDuration)),
                new FindOneAndUpdateOptions<ItemDocument> { Sort = Builders<ItemDocument>.Sort.Ascending(value => value.Position), ReturnDocument = ReturnDocument.After },
                cancellationToken).ConfigureAwait(false);
            if (document is null) break;
            claimed.Add(document.ToModel());
        }
        return claimed;
    }

    public async Task<int> RenewClaimsAsync(
        string jobId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var updated = await _items.UpdateManyAsync(
            value => value.JobId == jobId && value.State == ValidationJobItemState.Processing &&
                value.LeaseOwner == leaseOwner,
            Builders<ItemDocument>.Update.Set(
                value => value.LeaseExpiresAtUtc, _timeProvider.GetUtcNow().Add(leaseDuration)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return checked((int)updated.ModifiedCount);
    }

    public async Task<int> ReleaseClaimsAsync(
        string jobId,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        var updated = await _items.UpdateManyAsync(
            value => value.JobId == jobId && value.State == ValidationJobItemState.Processing &&
                value.LeaseOwner == leaseOwner,
            Builders<ItemDocument>.Update
                .Set(value => value.State, ValidationJobItemState.Pending)
                .Unset(value => value.LeaseOwner)
                .Unset(value => value.LeaseExpiresAtUtc),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return checked((int)updated.ModifiedCount);
    }

    public Task SetStateAsync(string jobId, ValidationJobState state, string? failureReason = null, CancellationToken cancellationToken = default) =>
        _jobs.UpdateOneAsync(value => value.Id == jobId,
            Builders<JobDocument>.Update.Set(value => value.State, state)
                .Set(value => value.FailureReason, failureReason)
                .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow()),
            cancellationToken: cancellationToken);

    public async Task<bool> TrySetFailedAsync(
        string jobId,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        var activeStates = new[]
        {
            ValidationJobState.Requested,
            ValidationJobState.Queued,
            ValidationJobState.Processing
        };
        var activeFilter = Builders<JobDocument>.Filter.Eq(value => value.Id, jobId)
            & Builders<JobDocument>.Filter.In(value => value.State, activeStates);
        var updated = await _jobs.UpdateOneAsync(
            activeFilter,
            Builders<JobDocument>.Update.Set(value => value.State, ValidationJobState.Failed)
                .Set(value => value.FailureReason, failureReason)
                .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow()),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated.ModifiedCount > 0;
    }

    public async Task<bool> CompleteClaimAsync(
        string jobId,
        int position,
        string leaseOwner,
        EmailValidationResult? result,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        var state = result is null ? ValidationJobItemState.Failed : ValidationJobItemState.Completed;
        var update = Builders<ItemDocument>.Update.Set(value => value.State, state)
            .Set(value => value.ResultJson, result is null ? null : JsonSerializer.Serialize(result, JsonOptions))
            .Set(value => value.ValidationId, result?.ValidationId)
            .Set(value => value.ResultState, result?.ResultState)
            .Set(value => value.ResultAttemptNumber, result?.AttemptNumber)
            .Set(value => value.Error, failureReason)
            .Unset(value => value.LeaseOwner)
            .Unset(value => value.LeaseExpiresAtUtc);
        var updated = await _items.UpdateOneAsync(
            value => value.JobId == jobId && value.Position == position &&
                value.State == ValidationJobItemState.Processing && value.LeaseOwner == leaseOwner,
            update, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (updated.ModifiedCount == 0) return false;

        var counters = Builders<JobDocument>.Update.Inc(value => value.ProcessedItems, 1)
            .Inc(value => value.FinalItems, result?.ResultState == ValidationResultState.Final ? 1 : 0)
            .Inc(value => value.ProvisionalItems, result?.ResultState == ValidationResultState.Provisional ? 1 : 0)
            .Inc(value => value.FailedItems, result is null ? 1 : 0)
            .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow());
        await _jobs.UpdateOneAsync(value => value.Id == jobId, counters, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<ValidationJobState?> TryFinalizeAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var unfinished = await _items.CountDocumentsAsync(
            value => value.JobId == jobId && (value.State == ValidationJobItemState.Pending ||
                value.State == ValidationJobItemState.Processing),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (unfinished > 0) return null;

        var job = await _jobs.Find(value => value.Id == jobId).FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (job is null) throw new ValidationJobNotFoundException(jobId);
        var failedItems = await _items.CountDocumentsAsync(
            value => value.JobId == jobId && value.State == ValidationJobItemState.Failed,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var finalItems = await _items.CountDocumentsAsync(
            value => value.JobId == jobId && value.State == ValidationJobItemState.Completed &&
                value.ResultState == ValidationResultState.Final,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var provisionalItems = await _items.CountDocumentsAsync(
            value => value.JobId == jobId && value.State == ValidationJobItemState.Completed &&
                value.ResultState == ValidationResultState.Provisional,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var finalState = failedItems == 0
            ? ValidationJobState.Completed
            : ValidationJobState.CompletedWithErrors;
        var activeStates = new[] { ValidationJobState.Requested, ValidationJobState.Queued, ValidationJobState.Processing };
        var updated = await _jobs.UpdateOneAsync(
            value => value.Id == jobId && activeStates.Contains(value.State),
            Builders<JobDocument>.Update.Set(value => value.State, finalState)
                .Set(value => value.FailureReason, null)
                .Set(value => value.ProcessedItems, job.TotalItems)
                .Set(value => value.FinalItems, checked((int)finalItems))
                .Set(value => value.ProvisionalItems, checked((int)provisionalItems))
                .Set(value => value.FailedItems, checked((int)failedItems))
                .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow()),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated.ModifiedCount > 0 ? finalState : null;
    }

    public async Task QueueDispatchAsync(
        string jobId,
        string dispatchId,
        int chunkCount,
        CancellationToken cancellationToken = default)
    {
        await _items.UpdateManyAsync(
            value => value.JobId == jobId && value.State == ValidationJobItemState.Processing,
            Builders<ItemDocument>.Update
                .Set(value => value.State, ValidationJobItemState.Pending)
                .Unset(value => value.LeaseOwner)
                .Unset(value => value.LeaseExpiresAtUtc),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var updated = await _jobs.UpdateOneAsync(
            value => value.Id == jobId && value.State == ValidationJobState.Failed,
            Builders<JobDocument>.Update
                .Set(value => value.State, ValidationJobState.Requested)
                .Set(value => value.FailureReason, null)
                .Set(value => value.ItemsReady, true)
                .Set(value => value.DispatchState, ValidationJobDispatchState.Pending)
                .Set(value => value.DispatchId, dispatchId)
                .Set(value => value.DispatchChunkCount, chunkCount)
                .Set(value => value.DispatchAttemptCount, 0)
                .Set(value => value.DispatchRetryAtUtc, null)
                .Set(value => value.DispatchLastError, null)
                .Unset(value => value.DispatchLeaseOwner)
                .Unset(value => value.DispatchLeaseExpiresAtUtc)
                .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow()),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (updated.ModifiedCount == 0)
            throw new InvalidOperationException($"Validation job '{jobId}' could not be queued from its current state.");
    }

    public async Task<IReadOnlyList<ValidationJobDispatch>> ClaimPendingAsync(
        int take,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var claimed = new List<ValidationJobDispatch>(Math.Max(0, take));
        for (var index = 0; index < take; index++)
        {
            var now = _timeProvider.GetUtcNow();
            var nowUtc = now.UtcDateTime;
            var available = Builders<JobDocument>.Filter.Eq(value => value.ItemsReady, true) &
                (Builders<JobDocument>.Filter.Eq(value => value.DispatchState, ValidationJobDispatchState.Pending) &
                 (Builders<JobDocument>.Filter.Eq(value => value.DispatchRetryAtUtc, null) |
                  Builders<JobDocument>.Filter.Lte(value => value.DispatchRetryAtUtc, nowUtc)) |
                 Builders<JobDocument>.Filter.Eq(value => value.DispatchState, ValidationJobDispatchState.Claimed) &
                 (Builders<JobDocument>.Filter.Eq(value => value.DispatchLeaseExpiresAtUtc, null) |
                  Builders<JobDocument>.Filter.Lte(value => value.DispatchLeaseExpiresAtUtc, nowUtc)));
            var document = await _jobs.FindOneAndUpdateAsync(
                available,
                Builders<JobDocument>.Update
                    .Set(value => value.DispatchState, ValidationJobDispatchState.Claimed)
                    .Set(value => value.DispatchLeaseOwner, leaseOwner)
                    .Set(value => value.DispatchLeaseExpiresAtUtc, now.Add(leaseDuration).UtcDateTime)
                    .Inc(value => value.DispatchAttemptCount, 1),
                new FindOneAndUpdateOptions<JobDocument>
                {
                    Sort = Builders<JobDocument>.Sort.Ascending(value => value.CreatedAtUtc),
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken).ConfigureAwait(false);
            if (document is null) break;
            if (string.IsNullOrWhiteSpace(document.DispatchId) || document.DispatchChunkCount < 1)
                throw new InvalidOperationException($"Validation job '{document.Id}' has an invalid dispatch outbox entry.");
            claimed.Add(new(document.Id, document.DispatchId, document.DispatchChunkCount,
                document.DispatchAttemptCount));
        }
        return claimed;
    }

    public async Task<bool> CompleteAsync(
        string jobId,
        string dispatchId,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        var claimed = Builders<JobDocument>.Filter.Eq(value => value.Id, jobId) &
            Builders<JobDocument>.Filter.Eq(value => value.DispatchId, dispatchId) &
            Builders<JobDocument>.Filter.Eq(value => value.DispatchState, ValidationJobDispatchState.Claimed) &
            Builders<JobDocument>.Filter.Eq(value => value.DispatchLeaseOwner, leaseOwner);
        var update = Builders<JobDocument>.Update
            .Set(value => value.DispatchState, ValidationJobDispatchState.Published)
            .Set(value => value.DispatchRetryAtUtc, null)
            .Set(value => value.DispatchLastError, null)
            .Unset(value => value.DispatchLeaseOwner)
            .Unset(value => value.DispatchLeaseExpiresAtUtc)
            .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow());
        var queued = await _jobs.UpdateOneAsync(
            claimed & Builders<JobDocument>.Filter.Eq(value => value.State, ValidationJobState.Requested),
            update.Set(value => value.State, ValidationJobState.Queued),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (queued.ModifiedCount > 0) return true;
        var published = await _jobs.UpdateOneAsync(claimed, update, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return published.ModifiedCount > 0;
    }

    public async Task<bool> ReleaseAsync(
        string jobId,
        string dispatchId,
        string leaseOwner,
        DateTimeOffset retryAtUtc,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        var updated = await _jobs.UpdateOneAsync(
            value => value.Id == jobId && value.DispatchId == dispatchId &&
                value.DispatchState == ValidationJobDispatchState.Claimed &&
                value.DispatchLeaseOwner == leaseOwner,
            Builders<JobDocument>.Update
                .Set(value => value.DispatchState, ValidationJobDispatchState.Pending)
                .Set(value => value.DispatchRetryAtUtc, retryAtUtc.UtcDateTime)
                .Set(value => value.DispatchLastError, failureReason)
                .Unset(value => value.DispatchLeaseOwner)
                .Unset(value => value.DispatchLeaseExpiresAtUtc)
                .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow()),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated.ModifiedCount > 0;
    }

    public async Task ProjectAsync(
        string jobId,
        string validationId,
        EmailValidationResult result,
        CancellationToken cancellationToken = default)
    {
        var direct = Builders<ItemDocument>.Filter.Eq(value => value.JobId, jobId) &
            Builders<ItemDocument>.Filter.Eq(value => value.ValidationId, validationId);
        var candidates = await _items.Find(direct).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            // Compatibility for rows written before ValidationId became an indexed field.
            candidates = (await _items.Find(value => value.JobId == jobId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Where(value => string.Equals(value.ToModel().Result?.ValidationId, validationId,
                    StringComparison.Ordinal))
                .ToList();
        }

        var serialized = JsonSerializer.Serialize(result, JsonOptions);
        var finalDelta = 0;
        var provisionalDelta = 0;
        var changed = 0L;
        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.ResultJson, serialized, StringComparison.Ordinal)) continue;
            var previous = candidate.ToModel().Result;
            if (previous is null || !string.Equals(previous.ValidationId, validationId, StringComparison.Ordinal))
                continue;
            var updated = await _items.UpdateOneAsync(
                value => value.Id == candidate.Id && value.ResultJson == candidate.ResultJson,
                Builders<ItemDocument>.Update
                    .Set(value => value.ResultJson, serialized)
                    .Set(value => value.ValidationId, validationId)
                    .Set(value => value.ResultState, result.ResultState)
                    .Set(value => value.ResultAttemptNumber, result.AttemptNumber)
                    .Set(value => value.Error, null),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (updated.ModifiedCount == 0) continue;
            changed++;
            finalDelta += (result.ResultState == ValidationResultState.Final ? 1 : 0) -
                (previous.ResultState == ValidationResultState.Final ? 1 : 0);
            provisionalDelta += (result.ResultState == ValidationResultState.Provisional ? 1 : 0) -
                (previous.ResultState == ValidationResultState.Provisional ? 1 : 0);
        }
        if (changed == 0) return;
        await _jobs.UpdateOneAsync(
            value => value.Id == jobId,
            Builders<JobDocument>.Update
                .Inc(value => value.FinalItems, finalDelta)
                .Inc(value => value.ProvisionalItems, provisionalDelta)
                .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow()),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    [BsonIgnoreExtraElements]
    internal sealed class JobDocument
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public ValidationJobState State { get; set; }
        public int TotalItems { get; set; }
        public int ProcessedItems { get; set; }
        public int FinalItems { get; set; }
        public int ProvisionalItems { get; set; }
        public int FailedItems { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public string? FailureReason { get; set; }
        public bool EnableSmtp { get; set; }
        public string? SourceFileId { get; set; }
        public string? SourceFileName { get; set; }
        public string? EmailColumn { get; set; }
        public string? TenantId { get; set; }
        public bool ItemsReady { get; set; }
        public ValidationJobDispatchState DispatchState { get; set; }
        public string? DispatchId { get; set; }
        public int DispatchChunkCount { get; set; }
        public int DispatchAttemptCount { get; set; }
        public string? DispatchLeaseOwner { get; set; }
        // MongoDB.Driver serializes DateTimeOffset as a two-element array. These fields share a
        // compound index with CreatedAtUtc (also DateTimeOffset), so they must remain scalar UTC
        // BSON dates to avoid MongoDB's parallel-array index restriction during lease updates.
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? DispatchLeaseExpiresAtUtc { get; set; }
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? DispatchRetryAtUtc { get; set; }
        public string? DispatchLastError { get; set; }
        public static JobDocument FromModel(ValidationJobSnapshot value) => new()
        {
            Id = value.JobId, CreatedAtUtc = value.CreatedAtUtc, State = value.State,
            TotalItems = value.TotalItems, ProcessedItems = value.ProcessedItems,
            FinalItems = value.FinalItems, ProvisionalItems = value.ProvisionalItems,
            FailedItems = value.FailedItems, UpdatedAtUtc = value.UpdatedAtUtc,
            FailureReason = value.FailureReason, EnableSmtp = value.EnableSmtp,
            SourceFileId = value.SourceFileId, SourceFileName = value.SourceFileName,
            EmailColumn = value.EmailColumn, TenantId = value.TenantId,
            DispatchState = value.DispatchState,
            DispatchId = value.DispatchId,
            DispatchChunkCount = value.DispatchChunkCount
        };
        public ValidationJobSnapshot ToModel() => new(Id, CreatedAtUtc, State, TotalItems, ProcessedItems,
            FinalItems, ProvisionalItems, FailedItems, UpdatedAtUtc, FailureReason, EnableSmtp,
            SourceFileId, SourceFileName, EmailColumn, DispatchState, DispatchId, DispatchChunkCount,
            TenantId);
    }

    [BsonIgnoreExtraElements]
    internal sealed class ItemDocument
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string JobId { get; set; } = string.Empty;
        public int Position { get; set; }
        public string Email { get; set; } = string.Empty;
        public ValidationJobItemState State { get; set; }
        public string? ResultJson { get; set; }
        public string? ValidationId { get; set; }
        public ValidationResultState? ResultState { get; set; }
        public int? ResultAttemptNumber { get; set; }
        public string? Error { get; set; }
        public string? LeaseOwner { get; set; }
        public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
        public static ItemDocument FromModel(ValidationJobItem value) => new()
        {
            Id = $"{value.JobId}:{value.Position}", JobId = value.JobId, Position = value.Position,
            Email = value.Email, State = value.State, Error = value.Error,
            ResultJson = value.Result is null ? null : JsonSerializer.Serialize(value.Result, JsonOptions),
            ValidationId = value.Result?.ValidationId,
            ResultState = value.Result?.ResultState,
            ResultAttemptNumber = value.Result?.AttemptNumber,
            LeaseOwner = value.LeaseOwner,
            LeaseExpiresAtUtc = value.LeaseExpiresAtUtc
        };
        public ValidationJobItem ToModel() => new(JobId, Position, Email, State,
            ResultJson is null ? null : JsonSerializer.Deserialize<EmailValidationResult>(ResultJson, JsonOptions),
            Error, LeaseOwner, LeaseExpiresAtUtc);
    }
}

public sealed class InMemoryValidationJobStore(TimeProvider timeProvider) : IValidationJobStore,
    IValidationJobResultSink, IValidationJobDispatchOutbox
{
    private readonly ConcurrentDictionary<string, ValidationJobSnapshot> _jobs = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, ValidationJobItem>> _items = new();
    private readonly ConcurrentDictionary<string, DispatchControl> _dispatches = new();
    private readonly object _sync = new();

    public Task CreateAsync(ValidationJobSnapshot job, IReadOnlyList<ValidationJobItem> items, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(job.DispatchId) || job.DispatchChunkCount < 1)
            throw new InvalidOperationException("A validation job requires a dispatch outbox entry.");
        if (!_jobs.TryAdd(job.JobId, job)) throw new InvalidOperationException("Duplicate job id.");
        _items[job.JobId] = new(items.ToDictionary(value => value.Position));
        _dispatches[job.JobId] = new(
            job.DispatchId, job.DispatchChunkCount, ValidationJobDispatchState.Pending, 0);
        return Task.CompletedTask;
    }
    public Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.GetValueOrDefault(jobId));
    public Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(
        string sourceFileId,
        string? tenantId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.Values
            .Where(job => string.Equals(job.SourceFileId, sourceFileId, StringComparison.Ordinal) &&
                string.Equals(job.TenantId, tenantId, StringComparison.Ordinal))
            .OrderByDescending(job => job.State is ValidationJobState.Completed or ValidationJobState.CompletedWithErrors)
            .ThenByDescending(job => job.CreatedAtUtc)
            .FirstOrDefault());
    public Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(string jobId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ValidationJobItem>>(_items.GetValueOrDefault(jobId)?.Values.OrderBy(value => value.Position).Skip(skip).Take(take).ToArray() ?? []);
    public Task<IReadOnlyList<ValidationJobItem>> ClaimPendingAsync(
        string jobId,
        int take,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var now = timeProvider.GetUtcNow();
            var available = _items.GetValueOrDefault(jobId)?.Values
                .Where(value => value.State == ValidationJobItemState.Pending ||
                    value.State == ValidationJobItemState.Processing &&
                    (value.LeaseExpiresAtUtc is null || value.LeaseExpiresAtUtc <= now))
                .OrderBy(value => value.Position)
                .Take(take)
                .ToArray() ?? [];
            foreach (var item in available)
                _items[jobId][item.Position] = item with
                {
                    State = ValidationJobItemState.Processing,
                    LeaseOwner = leaseOwner,
                    LeaseExpiresAtUtc = now.Add(leaseDuration)
                };
            return Task.FromResult<IReadOnlyList<ValidationJobItem>>(
                available.Select(item => _items[jobId][item.Position]).ToArray());
        }
    }

    public Task<int> RenewClaimsAsync(
        string jobId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_items.TryGetValue(jobId, out var items)) return Task.FromResult(0);
            var claimed = items.Values.Where(item => item.State == ValidationJobItemState.Processing &&
                item.LeaseOwner == leaseOwner).ToArray();
            foreach (var item in claimed)
                items[item.Position] = item with { LeaseExpiresAtUtc = timeProvider.GetUtcNow().Add(leaseDuration) };
            return Task.FromResult(claimed.Length);
        }
    }

    public Task<int> ReleaseClaimsAsync(
        string jobId,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_items.TryGetValue(jobId, out var items)) return Task.FromResult(0);
            var claimed = items.Values.Where(item => item.State == ValidationJobItemState.Processing &&
                item.LeaseOwner == leaseOwner).ToArray();
            foreach (var item in claimed)
                items[item.Position] = item with
                {
                    State = ValidationJobItemState.Pending,
                    LeaseOwner = null,
                    LeaseExpiresAtUtc = null
                };
            return Task.FromResult(claimed.Length);
        }
    }
    public Task SetStateAsync(string jobId, ValidationJobState state, string? failureReason = null, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryGetValue(jobId, out var job)) _jobs[jobId] = job with { State = state, UpdatedAtUtc = timeProvider.GetUtcNow(), FailureReason = failureReason };
        return Task.CompletedTask;
    }
    public Task<bool> TrySetFailedAsync(
        string jobId,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || job.State is
                ValidationJobState.Completed or ValidationJobState.CompletedWithErrors or ValidationJobState.Failed)
                return Task.FromResult(false);
            _jobs[jobId] = job with
            {
                State = ValidationJobState.Failed,
                FailureReason = failureReason,
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
            return Task.FromResult(true);
        }
    }
    public Task<bool> CompleteClaimAsync(
        string jobId,
        int position,
        string leaseOwner,
        EmailValidationResult? result,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_items.TryGetValue(jobId, out var items) || !items.TryGetValue(position, out var item) ||
                item.State != ValidationJobItemState.Processing || item.LeaseOwner != leaseOwner)
                return Task.FromResult(false);
            items[position] = item with
            {
                State = result is null ? ValidationJobItemState.Failed : ValidationJobItemState.Completed,
                Result = result,
                Error = failureReason,
                LeaseOwner = null,
                LeaseExpiresAtUtc = null
            };
            var job = _jobs[jobId];
            _jobs[jobId] = job with
            {
                ProcessedItems = job.ProcessedItems + 1,
                FinalItems = job.FinalItems + (result?.ResultState == ValidationResultState.Final ? 1 : 0),
                ProvisionalItems = job.ProvisionalItems + (result?.ResultState == ValidationResultState.Provisional ? 1 : 0),
                FailedItems = job.FailedItems + (result is null ? 1 : 0),
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
        }
        return Task.FromResult(true);
    }

    public Task<ValidationJobState?> TryFinalizeAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) throw new ValidationJobNotFoundException(jobId);
            if (_items[jobId].Values.Any(item => item.State is
                    ValidationJobItemState.Pending or ValidationJobItemState.Processing))
                return Task.FromResult<ValidationJobState?>(null);
            if (job.State is ValidationJobState.Completed or ValidationJobState.CompletedWithErrors or
                ValidationJobState.Failed)
                return Task.FromResult<ValidationJobState?>(null);
            var completedItems = _items[jobId].Values.ToArray();
            var failedItems = completedItems.Count(item => item.State == ValidationJobItemState.Failed);
            var finalItems = completedItems.Count(item => item.State == ValidationJobItemState.Completed &&
                item.Result?.ResultState == ValidationResultState.Final);
            var provisionalItems = completedItems.Count(item => item.State == ValidationJobItemState.Completed &&
                item.Result?.ResultState == ValidationResultState.Provisional);
            var finalState = failedItems == 0
                ? ValidationJobState.Completed
                : ValidationJobState.CompletedWithErrors;
            _jobs[jobId] = job with
            {
                State = finalState,
                FailureReason = null,
                ProcessedItems = job.TotalItems,
                FinalItems = finalItems,
                ProvisionalItems = provisionalItems,
                FailedItems = failedItems,
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
            return Task.FromResult<ValidationJobState?>(finalState);
        }
    }

    public Task QueueDispatchAsync(
        string jobId,
        string dispatchId,
        int chunkCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || job.State != ValidationJobState.Failed)
                throw new InvalidOperationException($"Validation job '{jobId}' could not be queued from its current state.");
            if (_items.TryGetValue(jobId, out var items))
            {
                foreach (var item in items.Values.Where(value => value.State == ValidationJobItemState.Processing).ToArray())
                    items[item.Position] = item with
                    {
                        State = ValidationJobItemState.Pending,
                        LeaseOwner = null,
                        LeaseExpiresAtUtc = null
                    };
            }
            _dispatches[jobId] = new(dispatchId, chunkCount, ValidationJobDispatchState.Pending, 0);
            _jobs[jobId] = job with
            {
                State = ValidationJobState.Requested,
                FailureReason = null,
                DispatchState = ValidationJobDispatchState.Pending,
                DispatchId = dispatchId,
                DispatchChunkCount = chunkCount,
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ValidationJobDispatch>> ClaimPendingAsync(
        int take,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var now = timeProvider.GetUtcNow();
            var available = _dispatches
                .Where(pair =>
                    pair.Value.State == ValidationJobDispatchState.Pending &&
                    (pair.Value.RetryAtUtc is null || pair.Value.RetryAtUtc <= now) ||
                    pair.Value.State == ValidationJobDispatchState.Claimed &&
                    (pair.Value.LeaseExpiresAtUtc is null || pair.Value.LeaseExpiresAtUtc <= now))
                .OrderBy(pair => _jobs[pair.Key].CreatedAtUtc)
                .Take(take)
                .ToArray();
            var results = new List<ValidationJobDispatch>(available.Length);
            foreach (var pair in available)
            {
                var claimed = pair.Value with
                {
                    State = ValidationJobDispatchState.Claimed,
                    AttemptCount = pair.Value.AttemptCount + 1,
                    LeaseOwner = leaseOwner,
                    LeaseExpiresAtUtc = now.Add(leaseDuration)
                };
                _dispatches[pair.Key] = claimed;
                _jobs[pair.Key] = _jobs[pair.Key] with { DispatchState = ValidationJobDispatchState.Claimed };
                results.Add(new(pair.Key, claimed.DispatchId, claimed.ChunkCount, claimed.AttemptCount));
            }
            return Task.FromResult<IReadOnlyList<ValidationJobDispatch>>(results);
        }
    }

    public Task<bool> CompleteAsync(
        string jobId,
        string dispatchId,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_dispatches.TryGetValue(jobId, out var dispatch) ||
                dispatch.DispatchId != dispatchId || dispatch.State != ValidationJobDispatchState.Claimed ||
                dispatch.LeaseOwner != leaseOwner)
                return Task.FromResult(false);
            _dispatches[jobId] = dispatch with
            {
                State = ValidationJobDispatchState.Published,
                LeaseOwner = null,
                LeaseExpiresAtUtc = null,
                RetryAtUtc = null,
                LastError = null
            };
            var job = _jobs[jobId];
            _jobs[jobId] = job with
            {
                State = job.State == ValidationJobState.Requested ? ValidationJobState.Queued : job.State,
                DispatchState = ValidationJobDispatchState.Published,
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseAsync(
        string jobId,
        string dispatchId,
        string leaseOwner,
        DateTimeOffset retryAtUtc,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_dispatches.TryGetValue(jobId, out var dispatch) ||
                dispatch.DispatchId != dispatchId || dispatch.State != ValidationJobDispatchState.Claimed ||
                dispatch.LeaseOwner != leaseOwner)
                return Task.FromResult(false);
            _dispatches[jobId] = dispatch with
            {
                State = ValidationJobDispatchState.Pending,
                LeaseOwner = null,
                LeaseExpiresAtUtc = null,
                RetryAtUtc = retryAtUtc,
                LastError = failureReason
            };
            _jobs[jobId] = _jobs[jobId] with
            {
                DispatchState = ValidationJobDispatchState.Pending,
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
            return Task.FromResult(true);
        }
    }

    public Task ProjectAsync(
        string jobId,
        string validationId,
        EmailValidationResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_items.TryGetValue(jobId, out var items) || !_jobs.TryGetValue(jobId, out var job))
                return Task.CompletedTask;
            var finalDelta = 0;
            var provisionalDelta = 0;
            var changed = false;
            foreach (var pair in items.ToArray())
            {
                var previous = pair.Value.Result;
                if (previous is null ||
                    !string.Equals(previous.ValidationId, validationId, StringComparison.Ordinal)) continue;
                items[pair.Key] = pair.Value with { Result = result, Error = null };
                finalDelta += (result.ResultState == ValidationResultState.Final ? 1 : 0) -
                    (previous.ResultState == ValidationResultState.Final ? 1 : 0);
                provisionalDelta += (result.ResultState == ValidationResultState.Provisional ? 1 : 0) -
                    (previous.ResultState == ValidationResultState.Provisional ? 1 : 0);
                changed = true;
            }
            if (changed)
                _jobs[jobId] = job with
                {
                    FinalItems = job.FinalItems + finalDelta,
                    ProvisionalItems = job.ProvisionalItems + provisionalDelta,
                    UpdatedAtUtc = timeProvider.GetUtcNow()
                };
        }
        return Task.CompletedTask;
    }

    private sealed record DispatchControl(
        string DispatchId,
        int ChunkCount,
        ValidationJobDispatchState State,
        int AttemptCount,
        string? LeaseOwner = null,
        DateTimeOffset? LeaseExpiresAtUtc = null,
        DateTimeOffset? RetryAtUtc = null,
        string? LastError = null);
}

public sealed class AzureServiceBusValidationJobDispatcher(IOptions<EmailValidationOptions> options) : IValidationJobDispatcher, IAsyncDisposable
{
    private readonly ValidationJobsOptions _options = options.Value.Jobs;
    private ServiceBusClient? _client;
    private ServiceBusSender? _sender;
    public async Task EnqueueAsync(
        string jobId,
        string dispatchId,
        int chunkCount,
        CancellationToken cancellationToken = default)
    {
        _client ??= new ServiceBusClient(_options.ServiceBusConnectionString);
        _sender ??= _client.CreateSender(_options.QueueName);
        for (var offset = 0; offset < chunkCount; offset += 100)
        {
            var count = Math.Min(100, chunkCount - offset);
            var messages = Enumerable.Range(0, count).Select(index => new ServiceBusMessage(BinaryData.FromString(jobId))
            {
                MessageId = $"{dispatchId}:{offset + index}",
                CorrelationId = jobId,
                Subject = "email-validation-job-chunk",
                ContentType = "text/plain"
            }).ToArray();
            await _sender.SendMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync().ConfigureAwait(false);
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class DisabledValidationJobDispatcher : IValidationJobDispatcher
{
    public Task EnqueueAsync(
        string jobId,
        string dispatchId,
        int chunkCount,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Asynchronous validation jobs are not enabled.");
}

public sealed class ValidationJobInfrastructureInitializer(
    IOptions<EmailValidationOptions> options,
    IValidationJobStore store) : IValidationJobInfrastructureInitializer
{
    private readonly EmailValidationOptions _options = options.Value;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Jobs.Enabled) return;
        if (store is MongoValidationJobStore mongo)
            await mongo.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!_options.Jobs.ProvisionQueue) return;
        var administration = new ServiceBusAdministrationClient(_options.Jobs.ServiceBusConnectionString);
        if (!await administration.QueueExistsAsync(_options.Jobs.QueueName, cancellationToken).ConfigureAwait(false))
        {
            await administration.CreateQueueAsync(new CreateQueueOptions(_options.Jobs.QueueName)
            {
                MaxDeliveryCount = _options.Jobs.MaxDeliveryCount,
                RequiresDuplicateDetection = true,
                DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10)
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
