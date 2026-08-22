using System.Text.Json;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    IRevalidationPersistenceInitializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IMongoCollection<ValidationLifecycleDocument> _collection;
    private readonly ILogger<MongoValidationLifecycleStore> _logger;
    private readonly TimeProvider _timeProvider;

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
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var indexes = new[]
        {
            new CreateIndexModel<ValidationLifecycleDocument>(
                Builders<ValidationLifecycleDocument>.IndexKeys
                    .Ascending(document => document.NormalizedEmail)
                    .Ascending(document => document.ResultState)
                    .Descending(document => document.UpdatedAt),
                new CreateIndexOptions { Name = "ix_lifecycle_email_state_updated" }),
            new CreateIndexModel<ValidationLifecycleDocument>(
                Builders<ValidationLifecycleDocument>.IndexKeys.Ascending(document => document.NormalizedEmail),
                new CreateIndexOptions<ValidationLifecycleDocument>
                {
                    Name = "ux_lifecycle_active_email",
                    Unique = true,
                    PartialFilterExpression = Builders<ValidationLifecycleDocument>.Filter.Eq(
                        document => document.ResultState, ValidationResultState.Provisional)
                }),
            new CreateIndexModel<ValidationLifecycleDocument>(
                Builders<ValidationLifecycleDocument>.IndexKeys
                    .Ascending(document => document.PendingMessageId)
                    .Ascending(document => document.DispatchLeaseUntil),
                new CreateIndexOptions { Name = "ix_lifecycle_pending_dispatch" })
        };
        await _collection.Indexes.CreateManyAsync(indexes, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Mongo validation lifecycle collection {Collection} initialized", _collection.CollectionNamespace.CollectionName);
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
                item => item.NormalizedEmail, normalizedEmail.ToLowerInvariant()),
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
                item => item.Id == lifecycle.ValidationId && item.Version == expectedVersion,
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

    public async Task<PendingRevalidation?> TryClaimAsync(
        string validationId,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var filter = Builders<ValidationLifecycleDocument>.Filter.And(
            Builders<ValidationLifecycleDocument>.Filter.Eq(item => item.Id, validationId),
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
                (item.DispatchLeaseUntil == null || item.DispatchLeaseUntil <= now))
            .SortBy(item => item.PendingScheduledAt)
            .Limit(Math.Max(1, maximumCount))
            .Project(item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> MarkScheduledAsync(
        string validationId,
        string messageId,
        RevalidationScheduleResult result,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = await GetAsync(validationId, cancellationToken).ConfigureAwait(false);
        if (lifecycle?.PendingRevalidation?.Message.MessageId != messageId) return false;
        var updated = lifecycle with
        {
            RetryScheduled = true,
            CurrentResult = lifecycle.CurrentResult with { RetryScheduled = true },
            PendingRevalidation = null,
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
                NormalizedEmail = lifecycle.NormalizedEmail.ToLowerInvariant(),
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
                CreatedAt = lifecycle.FirstValidatedAt,
                UpdatedAt = now
            };
        }

        private static EmailValidationResult Sanitize(EmailValidationResult result) => result with
        {
            SmtpEvidence = null,
            SmtpSessionEvidence = null,
            MxValidation = null,
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
    IRevalidationPersistenceInitializer
{
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
}
