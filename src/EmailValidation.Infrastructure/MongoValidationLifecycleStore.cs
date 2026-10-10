using System.Text.Json;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public interface IRevalidationPersistenceInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class MongoValidationLifecycleStore :
    IValidationLifecycleStore,
    IRevalidationOutbox,
    IRevalidationRecoveryStore,
    IRevalidationPersistenceInitializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IMongoCollection<ValidationLifecycleDocument> _collection;
    private readonly ILogger<MongoValidationLifecycleStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumExecutionRecoveries;

    public MongoValidationLifecycleStore(
        IMongoClient client,
        IOptions<EmailValidationOptions> options,
        TimeProvider timeProvider,
        ILogger<MongoValidationLifecycleStore> logger)
    {
        var persistence = options.Value.Persistence;
        _collection = client.GetDatabase(persistence.DatabaseName)
            .GetCollection<ValidationLifecycleDocument>(persistence.LifecycleCollection);
        _logger = logger;
        _timeProvider = timeProvider;
        _maximumExecutionRecoveries = options.Value.Revalidation.MaximumExecutionRecoveries;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var indexes = new[]
        {
            new CreateIndexModel<ValidationLifecycleDocument>(
                Builders<ValidationLifecycleDocument>.IndexKeys.Ascending(x => x.ResultState)
                    .Ascending(x => x.LifecycleState).Ascending(x => x.RetryDueAtUtc),
                new CreateIndexOptions { Name = "ix_lifecycle_retry_recovery_v2" }),
            new CreateIndexModel<ValidationLifecycleDocument>(
                Builders<ValidationLifecycleDocument>.IndexKeys.Ascending(x => x.ResultState)
                    .Ascending(x => x.LifecycleState).Ascending(x => x.ExecutionLeaseExpiresAtUtc),
                new CreateIndexOptions { Name = "ix_lifecycle_execution_recovery_v2" }),
            new CreateIndexModel<ValidationLifecycleDocument>(
                Builders<ValidationLifecycleDocument>.IndexKeys
                    .Ascending(document => document.NormalizedEmail)
                    .Ascending(document => document.ResultState)
                    .Descending(document => document.UpdatedAt),
                new CreateIndexOptions { Name = "ix_lifecycle_email_state_updated" }),
            new CreateIndexModel<ValidationLifecycleDocument>(
                Builders<ValidationLifecycleDocument>.IndexKeys.Ascending(document => document.MailboxKey),
                new CreateIndexOptions<ValidationLifecycleDocument>
                {
                    Name = "ux_lifecycle_active_mailbox_v2",
                    Unique = true,
                    Collation = Collation.Simple,
                    PartialFilterExpression = Builders<ValidationLifecycleDocument>.Filter.Eq(
                        document => document.ResultState, ValidationResultState.Provisional) &
                        Builders<ValidationLifecycleDocument>.Filter.Type(document => document.MailboxKey, BsonType.String)
                }),
            new CreateIndexModel<ValidationLifecycleDocument>(
                Builders<ValidationLifecycleDocument>.IndexKeys
                    .Ascending(document => document.PendingMessageId)
                    .Ascending(document => document.DispatchLeaseUntil),
                new CreateIndexOptions { Name = "ix_lifecycle_pending_dispatch" })
        };
        await _collection.Indexes.CreateManyAsync(indexes, cancellationToken).ConfigureAwait(false);
        await MailboxIdentityIndexMigration.DropLegacyAsync(_collection, "ux_lifecycle_active_email", cancellationToken).ConfigureAwait(false);
        await InitializeRecoveryMetadataAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Mongo validation lifecycle collection {Collection} initialized", _collection.CollectionNamespace.CollectionName);
    }

    private async Task InitializeRecoveryMetadataAsync(CancellationToken cancellationToken)
    {
        var missing = Builders<ValidationLifecycleDocument>.Filter.Exists(x => x.RecoverySchemaVersion, false);
        using var cursor = await _collection.Find(missing).ToCursorAsync(cancellationToken).ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in cursor.Current)
            {
                var update = Builders<ValidationLifecycleDocument>.Update.Set(x => x.RecoverySchemaVersion, 1);
                try
                {
                    var lifecycle = document.ToModel();
                    update = update.Set(x => x.LifecycleState, lifecycle.LifecycleState)
                        .Set(x => x.RetryDueAtUtc, lifecycle.NextRetryAt?.UtcDateTime);
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentNullException)
                {
                    _logger.LogWarning("A legacy lifecycle has invalid recovery metadata; no retry was reconstructed");
                }
                await _collection.UpdateOneAsync(missing & Builders<ValidationLifecycleDocument>.Filter.Where(
                    x => x.Id == document.Id && x.Version == document.Version), update,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<ValidationLifecycle?> GetAsync(
        string validationId,
        CancellationToken cancellationToken = default)
    {
        var document = await _collection.Find(item => item.Id == validationId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document?.ToModel();
    }

    public async Task<ValidationLifecycle?> GetActiveByEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<ValidationLifecycleDocument>.Filter.And(
            Builders<ValidationLifecycleDocument>.Filter.Eq(
                item => item.MailboxKey, MailboxIdentity.TryCreate(normalizedEmail)?.Key ?? normalizedEmail),
            Builders<ValidationLifecycleDocument>.Filter.Eq(
                item => item.ResultState, ValidationResultState.Provisional));
        var document = await _collection.Find(filter)
            .SortByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document?.ToModel();
    }

    public async Task<LifecycleWriteResult> TrySaveAsync(
        ValidationLifecycle lifecycle,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var document = ValidationLifecycleDocument.FromModel(lifecycle, _timeProvider.GetUtcNow());
        try
        {
            if (expectedVersion == 0)
            {
                await _collection.InsertOneAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new(true, document.ToModel());
            }

            var result = await _collection.ReplaceOneAsync(
                item => item.Id == lifecycle.ValidationId && item.Version == expectedVersion && item.ExecutionOwner == null,
                document,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ModifiedCount == 1
                ? new(true, document.ToModel())
                : new(false, null);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new(false, null);
        }
    }

    public async Task<ValidationLifecycle?> TryAcquireExecutionAsync(string validationId, long expectedVersion,
        int attemptNumber, string ownerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(validationId, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        if (current is null || current.Version != expectedVersion) return null;
        var acquired = RevalidationExecution.Acquire(current, attemptNumber, ownerId, lease, now);
        if (acquired is null) return null;
        var result = await _collection.ReplaceOneAsync(
            Builders<ValidationLifecycleDocument>.Filter.Where(item => item.Id == validationId && item.Version == expectedVersion &&
                (item.ExecutionOwner == null || item.ExecutionLeaseExpiresAtUtc <= now.UtcDateTime)) &
                ExpiresAfterServerNow(acquired.ExecutionLease!.ExpiresAt),
            ValidationLifecycleDocument.FromModel(acquired, now), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount == 1 ? acquired : null;
    }

    public async Task<bool> RenewExecutionAsync(string validationId, RevalidationExecutionLease lease,
        TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var result = await _collection.UpdateOneAsync(
            Owned(validationId, lease, now) & ExpiresAfterServerNow(now.Add(duration)),
            Builders<ValidationLifecycleDocument>.Update.Set(item => item.ExecutionLeaseExpiresAtUtc, now.Add(duration).UtcDateTime),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.MatchedCount == 1;
    }

    public async Task<LifecycleWriteResult> TrySaveExecutionAsync(ValidationLifecycle lifecycle, long expectedVersion,
        RevalidationExecutionLease lease, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var document = ValidationLifecycleDocument.FromModel(lifecycle, now);
        var result = await _collection.ReplaceOneAsync(
            Owned(lifecycle.ValidationId, lease, now) & Builders<ValidationLifecycleDocument>.Filter.Eq(item => item.Version, expectedVersion),
            document, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(result.ModifiedCount == 1, result.ModifiedCount == 1 ? document.ToModel() : null);
    }

    private static FilterDefinition<ValidationLifecycleDocument> Owned(string id, RevalidationExecutionLease lease, DateTimeOffset now) =>
        Builders<ValidationLifecycleDocument>.Filter.Where(item => item.Id == id &&
            item.ExecutionOwner == lease.OwnerId && item.ExecutionFence == lease.FencingToken &&
            item.ExecutionLeaseExpiresAtUtc > now.UtcDateTime && item.LifecycleState == ValidationLifecycleState.Revalidating) &
        new BsonDocumentFilterDefinition<ValidationLifecycleDocument>(new BsonDocument("$expr",
            new BsonDocument("$gt", new BsonArray { "$ExecutionLeaseExpiresAtUtc", "$$NOW" })));

    private static FilterDefinition<ValidationLifecycleDocument> NoLiveExecution() =>
        Builders<ValidationLifecycleDocument>.Filter.Eq(x => x.ExecutionOwner, null) |
        new BsonDocumentFilterDefinition<ValidationLifecycleDocument>(new BsonDocument("$expr",
            new BsonDocument("$lte", new BsonArray { "$ExecutionLeaseExpiresAtUtc", "$$NOW" })));

    private static FilterDefinition<ValidationLifecycleDocument> ExpiresAfterServerNow(DateTimeOffset expires) =>
        new BsonDocumentFilterDefinition<ValidationLifecycleDocument>(new BsonDocument("$expr",
            new BsonDocument("$gt", new BsonArray { new BsonDateTime(expires.UtcDateTime), "$$NOW" })));

    public async Task<PendingRevalidation?> TryClaimAsync(
        string validationId,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var filter = Builders<ValidationLifecycleDocument>.Filter.And(
            Builders<ValidationLifecycleDocument>.Filter.Eq(item => item.Id, validationId),
            Builders<ValidationLifecycleDocument>.Filter.Ne(item => item.LifecycleState, ValidationLifecycleState.Revalidating),
            Builders<ValidationLifecycleDocument>.Filter.Ne(item => item.PendingMessageId, null),
            Builders<ValidationLifecycleDocument>.Filter.Or(
                Builders<ValidationLifecycleDocument>.Filter.Eq(item => item.DispatchLeaseUntil, null),
                Builders<ValidationLifecycleDocument>.Filter.Lte(item => item.DispatchLeaseUntil, now)));
        var update = Builders<ValidationLifecycleDocument>.Update
            .Set(item => item.DispatchLeaseUntil, now.Add(lease))
            .Inc(item => item.DispatchAttempts, 1)
            .Inc(item => item.Version, 1)
            .Set(item => item.UpdatedAt, now);
        var claimed = await _collection.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<ValidationLifecycleDocument, ValidationLifecycleDocument>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken).ConfigureAwait(false);
        return claimed?.ToModel().PendingRevalidation;
    }

    public async Task<IReadOnlyList<string>> GetPendingValidationIdsAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        return await _collection.Find(item =>
                item.PendingMessageId != null &&
                item.LifecycleState != ValidationLifecycleState.Revalidating &&
                (item.DispatchLeaseUntil == null || item.DispatchLeaseUntil <= now))
            .SortBy(item => item.PendingScheduledAt)
            .Limit(Math.Max(1, maximumCount))
            .Project(item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> RecoverOverdueAsync(
        int maximumCount,
        TimeSpan minimumOverdue,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Max(1, maximumCount);
        var now = _timeProvider.GetUtcNow();
        var cutoff = (now - minimumOverdue).UtcDateTime;
        var candidates = await _collection.Find(Builders<ValidationLifecycleDocument>.Filter.Where(document =>
                document.ResultState == ValidationResultState.Provisional &&
                (document.LifecycleState == ValidationLifecycleState.RetryWaiting && document.PendingMessageId == null && document.RetryDueAtUtc <= cutoff ||
                 document.LifecycleState == ValidationLifecycleState.Revalidating &&
                    (document.ExecutionLeaseExpiresAtUtc <= now.UtcDateTime ||
                     document.ExecutionOwner == null && document.RetryDueAtUtc <= cutoff))) & NoLiveExecution())
            .Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var recovered = new List<string>();
        foreach (var candidate in candidates)
        {
            if (recovered.Count >= take) break;
            var current = candidate.ToModel();
            var replacement = TryCreateRecovery(current, now, minimumOverdue, _maximumExecutionRecoveries);
            if (replacement is null) continue;
            var saved = await _collection.ReplaceOneAsync(Builders<ValidationLifecycleDocument>.Filter.Where(document => document.Id == current.ValidationId &&
                    document.Version == current.Version &&
                    (document.ExecutionOwner == null || document.ExecutionLeaseExpiresAtUtc <= now.UtcDateTime)) & NoLiveExecution(),
                ValidationLifecycleDocument.FromModel(replacement, now), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (saved.ModifiedCount == 1) recovered.Add(current.ValidationId);
        }
        return recovered;
    }

    internal static ValidationLifecycle? TryCreateRecovery(
        ValidationLifecycle lifecycle,
        DateTimeOffset now,
        TimeSpan minimumOverdue,
        int maximumExecutionRecoveries = 3)
    {
        var abandoned = lifecycle.LifecycleState == ValidationLifecycleState.Revalidating;
        if (lifecycle.ResultState != ValidationResultState.Provisional ||
            (!abandoned && (lifecycle.LifecycleState != ValidationLifecycleState.RetryWaiting || !lifecycle.RetryScheduled)) ||
            (!abandoned && lifecycle.PendingRevalidation is not null) ||
            lifecycle.NextRetryAt is not { } scheduledAt ||
            (abandoned && lifecycle.ExecutionLease is { } lease
                ? lease.ExpiresAt > now : scheduledAt > now || now - scheduledAt < minimumOverdue) ||
            (!abandoned && lifecycle.AttemptNumber >= lifecycle.MaximumAttempts) ||
            lifecycle.FirstValidatedAt == default || lifecycle.LastValidatedAt == default)
            return null;

        var recoveries = lifecycle.ExecutionRecoveryCount + (abandoned ? 1 : 0);
        if (abandoned && recoveries > maximumExecutionRecoveries)
            return lifecycle with
            {
                ExecutionLease = null, ExecutionRecoveryCount = recoveries, AttemptNumber = lifecycle.AttemptNumber - 1,
                ResultState = ValidationResultState.Final, LifecycleState = ValidationLifecycleState.Failed,
                CurrentStage = ValidationProgressStage.Failed, RetryScheduled = false, NextRetryAt = null,
                FinalizedAt = now, LastUpdatedAt = now, Sequence = lifecycle.Sequence + 1, Version = lifecycle.Version + 1,
                StatusMessage = "Automatic revalidation stopped after repeated abandoned executions.",
                CurrentResult = lifecycle.CurrentResult with
                {
                    ResultState = ValidationResultState.Final, RetryScheduled = false, RetryAfter = null, FinalizedAt = now,
                    AttemptNumber = lifecycle.AttemptNumber - 1,
                    UnknownContext = lifecycle.CurrentResult.Status == EmailValidationStatus.Unknown
                        ? new(UnknownCause.ExecutionFailure, "Automatic revalidation stopped after repeated abandoned executions.",
                            false, "Submit a new validation after the worker failure is resolved.")
                        : lifecycle.CurrentResult.UnknownContext
                }
            };

        var message = new EmailRevalidationMessageV1(
            lifecycle.ValidationId,
            abandoned ? lifecycle.AttemptNumber : lifecycle.AttemptNumber + 1,
            lifecycle.MaximumAttempts,
            lifecycle.FirstValidatedAt,
            lifecycle.LastValidatedAt,
            now,
            lifecycle.CurrentResult.MailProvider.ToString(),
            lifecycle.CurrentResult.Status,
            lifecycle.CurrentResult.SubStatus,
            lifecycle.CurrentResult.Metadata?.Policy.ClassificationPolicyVersion,
            MessageVersion: 2, DispatchGeneration: checked(lifecycle.DispatchGeneration + 1));
        return lifecycle with
        {
            DispatchGeneration = message.DispatchGeneration,
            ExecutionLease = null,
            ExecutionRecoveryCount = recoveries,
            AttemptNumber = abandoned ? lifecycle.AttemptNumber - 1 : lifecycle.AttemptNumber,
            NextRetryAt = now,
            PendingRevalidation = new(message, now, now),
            RetryScheduled = false,
            CurrentResult = lifecycle.CurrentResult with
            {
                RetryAfter = now,
                AttemptNumber = abandoned ? lifecycle.AttemptNumber - 1 : lifecycle.AttemptNumber,
                RetryScheduled = false,
                UnknownContext = lifecycle.CurrentResult.UnknownContext is null
                    ? null
                    : lifecycle.CurrentResult.UnknownContext with { RetryAfter = now }
            },
            LifecycleState = ValidationLifecycleState.Provisional,
            CurrentStage = ValidationProgressStage.Provisional,
            StatusMessage = "An overdue automatic revalidation was recovered for durable rescheduling.",
            LastUpdatedAt = now,
            Sequence = lifecycle.Sequence + 1,
            Version = lifecycle.Version + 1
        };
    }

    public async Task<bool> MarkScheduledAsync(
        string validationId,
        string messageId,
        RevalidationScheduleResult result,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = await GetAsync(validationId, cancellationToken).ConfigureAwait(false);
        if (lifecycle?.PendingRevalidation?.Message.MessageId != messageId ||
            lifecycle.LifecycleState == ValidationLifecycleState.Revalidating) return false;
        var updated = lifecycle with
        {
            RetryScheduled = true,
            CurrentResult = lifecycle.CurrentResult with { RetryScheduled = true },
            PendingRevalidation = null,
            LifecycleState = ValidationLifecycleState.RetryWaiting,
            CurrentStage = ValidationProgressStage.RetryWaiting,
            LastUpdatedAt = _timeProvider.GetUtcNow(),
            StatusMessage = "Validation is queued for automatic revalidation.",
            Sequence = lifecycle.Sequence + 1,
            Version = lifecycle.Version + 1
        };
        return (await TrySaveAsync(updated, lifecycle.Version, cancellationToken).ConfigureAwait(false)).Applied;
    }

    public async Task ReleaseAsync(
        string validationId,
        string messageId,
        string? errorCode,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var update = Builders<ValidationLifecycleDocument>.Update
            .Set(item => item.DispatchLeaseUntil, null)
            .Set(item => item.LastDispatchErrorCode, errorCode)
            .Inc(item => item.Version, 1)
            .Set(item => item.UpdatedAt, now);
        await _collection.UpdateOneAsync(
            item => item.Id == validationId && item.PendingMessageId == messageId,
            update,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal sealed class ValidationLifecycleDocument
    {
        [BsonId]
        public required string Id { get; init; }
        public required string NormalizedEmail { get; init; }
        public string? MailboxKey { get; init; }
        public int RecoverySchemaVersion { get; init; }
        public ValidationLifecycleState LifecycleState { get; init; }
        public DateTime? RetryDueAtUtc { get; init; }
        public string? ExecutionOwner { get; init; }
        public long ExecutionFence { get; init; }
        public DateTime? ExecutionLeaseExpiresAtUtc { get; init; }
        public ValidationResultState ResultState { get; init; }
        public int AttemptNumber { get; init; }
        public int MaximumAttempts { get; init; }
        public long Version { get; set; }
        public string? PendingMessageId { get; init; }
        public DateTimeOffset? PendingScheduledAt { get; init; }
        public DateTimeOffset? DispatchLeaseUntil { get; set; }
        public int DispatchAttempts { get; set; }
        public string? LastDispatchErrorCode { get; set; }
        public required string PayloadJson { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }

        public ValidationLifecycle ToModel()
        {
            var model = JsonSerializer.Deserialize<ValidationLifecycle>(PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException($"Lifecycle '{Id}' contains an invalid payload.");
            return model with
            {
                Version = Version,
                ExecutionFence = ExecutionFence,
                ExecutionLease = ExecutionOwner is not null && ExecutionLeaseExpiresAtUtc is { } expires
                    ? new(ExecutionOwner, ExecutionFence, new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc))) : null,
                PendingRevalidation = model.PendingRevalidation is null ? null : model.PendingRevalidation with
                {
                    DispatchLeaseUntil = DispatchLeaseUntil,
                    DispatchAttempts = DispatchAttempts,
                    LastErrorCode = LastDispatchErrorCode
                }
            };
        }

        public static ValidationLifecycleDocument FromModel(ValidationLifecycle lifecycle, DateTimeOffset now)
        {
            var sanitized = lifecycle with { CurrentResult = Sanitize(lifecycle.CurrentResult) };
            return new()
            {
                Id = lifecycle.ValidationId,
                NormalizedEmail = MailboxIdentity.NormalizeOrOriginal(lifecycle.NormalizedEmail),
                MailboxKey = lifecycle.MailboxKey,
                RecoverySchemaVersion = 1,
                LifecycleState = lifecycle.LifecycleState,
                RetryDueAtUtc = lifecycle.NextRetryAt?.UtcDateTime,
                ExecutionOwner = lifecycle.ExecutionLease?.OwnerId,
                ExecutionFence = lifecycle.ExecutionFence,
                ExecutionLeaseExpiresAtUtc = lifecycle.ExecutionLease?.ExpiresAt.UtcDateTime,
                ResultState = lifecycle.ResultState,
                AttemptNumber = lifecycle.AttemptNumber,
                MaximumAttempts = lifecycle.MaximumAttempts,
                Version = lifecycle.Version,
                PendingMessageId = lifecycle.PendingRevalidation?.Message.MessageId,
                PendingScheduledAt = lifecycle.PendingRevalidation?.ScheduledAt,
                DispatchLeaseUntil = lifecycle.PendingRevalidation?.DispatchLeaseUntil,
                DispatchAttempts = lifecycle.PendingRevalidation?.DispatchAttempts ?? 0,
                LastDispatchErrorCode = lifecycle.PendingRevalidation?.LastErrorCode,
                PayloadJson = JsonSerializer.Serialize(sanitized, JsonOptions),
                CreatedAt = lifecycle.RequestedAt ?? lifecycle.FirstValidatedAt,
                UpdatedAt = now
            };
        }

        private static EmailValidationResult Sanitize(EmailValidationResult result) => result with
        {
            SmtpEvidence = null,
            SmtpSessionEvidence = null,
            MxValidation = result.MxValidation is null
                ? null
                : result.MxValidation with { Attempts = [] },
            CatchAllEvidence = null,
            ProbeSenderHealth = null,
            Diagnostics = null,
            Evidence = [],
            ConfidenceEvidence = [],
            DomainIntelligence = result.DomainIntelligence is null ? null : result.DomainIntelligence with
            {
                CatchAll = result.DomainIntelligence.CatchAll with { ProbeResults = [] }
            }
        };
    }
}

public sealed class NoOpValidationLifecycleStore :
    IValidationLifecycleStore,
    IRevalidationOutbox,
    IRevalidationRecoveryStore,
    IRevalidationPersistenceInitializer
{
    public Task<ValidationLifecycle?> TryAcquireExecutionAsync(string validationId, long expectedVersion,
        int attemptNumber, string ownerId, TimeSpan lease, CancellationToken cancellationToken = default) =>
        Task.FromResult<ValidationLifecycle?>(null);
    public Task<bool> RenewExecutionAsync(string validationId, RevalidationExecutionLease lease,
        TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<LifecycleWriteResult> TrySaveExecutionAsync(ValidationLifecycle lifecycle, long expectedVersion,
        RevalidationExecutionLease lease, CancellationToken cancellationToken = default) =>
        Task.FromResult(new LifecycleWriteResult(false, null));
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ValidationLifecycle?> GetAsync(string validationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ValidationLifecycle?>(null);
    public Task<ValidationLifecycle?> GetActiveByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        Task.FromResult<ValidationLifecycle?>(null);
    public Task<LifecycleWriteResult> TrySaveAsync(ValidationLifecycle lifecycle, long expectedVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult(new LifecycleWriteResult(false, null));
    public Task<PendingRevalidation?> TryClaimAsync(string validationId, TimeSpan lease, CancellationToken cancellationToken = default) =>
        Task.FromResult<PendingRevalidation?>(null);
    public Task<IReadOnlyList<string>> GetPendingValidationIdsAsync(int maximumCount, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    public Task<bool> MarkScheduledAsync(string validationId, string messageId, RevalidationScheduleResult result, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
    public Task ReleaseAsync(string validationId, string messageId, string? errorCode, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task<IReadOnlyList<string>> RecoverOverdueAsync(int maximumCount, TimeSpan minimumOverdue, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
