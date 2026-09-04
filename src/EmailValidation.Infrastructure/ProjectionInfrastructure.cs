using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public sealed class ProjectionInfrastructureInitializer(
    IOptions<EmailValidationOptions> options,
    IProjectionPersistenceInitializer persistence,
    IProjectionReconciler reconciler,
    ILogger<ProjectionInfrastructureInitializer> logger)
{
    private readonly EmailValidationProjectionOptions _options = options.Value.Projection;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;
        await persistence.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (reconciler is IProjectionPersistenceInitializer reconciliationPersistence)
            await reconciliationPersistence.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!_options.ServiceBus.ProvisionEntities) return;
        var administration = new ServiceBusAdministrationClient(_options.ServiceBus.ConnectionString);
        if (!await administration.TopicExistsAsync(_options.ServiceBus.TopicName, cancellationToken).ConfigureAwait(false))
            await administration.CreateTopicAsync(_options.ServiceBus.TopicName, cancellationToken).ConfigureAwait(false);
        if (!await administration.SubscriptionExistsAsync(
                _options.ServiceBus.TopicName, _options.ServiceBus.SubscriptionName, cancellationToken).ConfigureAwait(false))
        {
            await administration.CreateSubscriptionAsync(new CreateSubscriptionOptions(
                _options.ServiceBus.TopicName, _options.ServiceBus.SubscriptionName)
            {
                MaxDeliveryCount = _options.ServiceBus.MaxDeliveryCount
            }, cancellationToken).ConfigureAwait(false);
        }
        logger.LogInformation("Observation topic {Topic} and subscription {Subscription} are available",
            _options.ServiceBus.TopicName, _options.ServiceBus.SubscriptionName);
    }
}

public sealed class HmacEmailCorrelationService(
    IOptions<EmailValidationOptions> options,
    ILogger<HmacEmailCorrelationService> logger) : IEmailCorrelationService
{
    private static readonly Meter Meter = new("EmailValidation.Projection", "1.0.0");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        "email_validation_projection_correlation_failure_total");
    private readonly ProjectionPrivacyOptions _options = options.Value.Projection.Privacy;

    public ValueTask<EmailCorrelation?> TryCreateAsync(
        string? tenantId,
        string normalizedEmail,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_options.EmailHashKey) ||
            Encoding.UTF8.GetByteCount(_options.EmailHashKey) < 32)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("failure_category", "key_unavailable"));
            logger.LogError(
                "Email observation correlation key is unavailable; correlation is omitted and validation remains unaffected");
            return ValueTask.FromResult<EmailCorrelation?>(null);
        }

        var scopedValue = $"{tenantId ?? string.Empty}\n{normalizedEmail.Trim().ToLowerInvariant()}";
        var digest = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.EmailHashKey),
            Encoding.UTF8.GetBytes(scopedValue));
        return ValueTask.FromResult<EmailCorrelation?>(new(
            Convert.ToHexString(digest).ToLowerInvariant(), _options.EmailHashKeyVersion));
    }
}

public sealed class DisabledProjectionOutbox : IProjectionOutbox, IProjectionPersistenceInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> EnqueueAsync(EmailValidationObservationEnvelope observation, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
    public Task<IReadOnlyList<ProjectionOutboxEntry>> ClaimAsync(int maximumCount, string lockOwner,
        TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProjectionOutboxEntry>>([]);
    public Task MarkPublishedAsync(string eventId, string lockOwner, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task ReleaseAsync(string eventId, string lockOwner, DateTimeOffset nextAttemptAtUtc, string errorCode,
        bool terminal, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ProjectionOutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProjectionOutboxBacklog(0, null));
}

public sealed class DisabledProjectionReconciler : IProjectionReconciler
{
    public Task<ProjectionReplayResult> ReconcileAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProjectionReplayResult(0, 0, 0, null, null, false));
    public Task<ProjectionReplayResult> BackfillAsync(ProjectionReplayRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProjectionReplayResult(0, 0, 0, null, null, request.DryRun));
}

public sealed class MongoProjectionOutbox : IProjectionOutbox, IProjectionPersistenceInitializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IMongoCollection<ProjectionOutboxDocument> _collection;
    private readonly ProjectionOutboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MongoProjectionOutbox> _logger;

    public MongoProjectionOutbox(
        IMongoClient client,
        IOptions<EmailValidationOptions> options,
        TimeProvider timeProvider,
        ILogger<MongoProjectionOutbox> logger)
    {
        var configured = options.Value;
        _options = configured.Projection.Outbox;
        _collection = client.GetDatabase(configured.Persistence.DatabaseName)
            .GetCollection<ProjectionOutboxDocument>(_options.CollectionName);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var indexes = new[]
        {
            new CreateIndexModel<ProjectionOutboxDocument>(
                Builders<ProjectionOutboxDocument>.IndexKeys
                    .Ascending(item => item.State)
                    .Ascending(item => item.NextPublishAttemptAtUtc)
                    .Ascending(item => item.LockExpiresAtUtc),
                new CreateIndexOptions { Name = "ix_projection_outbox_claim" }),
            new CreateIndexModel<ProjectionOutboxDocument>(
                Builders<ProjectionOutboxDocument>.IndexKeys.Ascending(item => item.CreatedAtUtc),
                new CreateIndexOptions { Name = "ix_projection_outbox_created" }),
            new CreateIndexModel<ProjectionOutboxDocument>(
                Builders<ProjectionOutboxDocument>.IndexKeys.Ascending(item => item.PublishedAtUtc),
                new CreateIndexOptions
                {
                    Name = "ttl_projection_outbox_published",
                    ExpireAfter = TimeSpan.FromDays(_options.PublishedRetentionDays)
                })
        };
        await _collection.Indexes.CreateManyAsync(indexes, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Mongo projection outbox {Collection} initialized", _options.CollectionName);
    }

    public async Task<bool> EnqueueAsync(
        EmailValidationObservationEnvelope observation,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        try
        {
            await _collection.InsertOneAsync(new ProjectionOutboxDocument
            {
                Id = observation.EventId,
                EventType = observation.EventType,
                SchemaVersion = observation.SchemaVersion,
                OccurredAtUtc = observation.OccurredAtUtc,
                PayloadJson = JsonSerializer.Serialize(observation, JsonOptions),
                State = ProjectionOutboxState.Pending,
                NextPublishAttemptAtUtc = now,
                CreatedAtUtc = now
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            ProjectionTelemetry.ObservationCreated(observation.EventType, observation.SchemaVersion);
            return true;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ProjectionOutboxEntry>> ClaimAsync(
        int maximumCount,
        string lockOwner,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default)
    {
        var claimed = new List<ProjectionOutboxEntry>(Math.Max(1, maximumCount));
        for (var index = 0; index < Math.Max(1, maximumCount); index++)
        {
            var now = _timeProvider.GetUtcNow();
            var filter = Builders<ProjectionOutboxDocument>.Filter.And(
                Builders<ProjectionOutboxDocument>.Filter.Or(
                    Builders<ProjectionOutboxDocument>.Filter.And(
                        Builders<ProjectionOutboxDocument>.Filter.Eq(item => item.State, ProjectionOutboxState.Pending),
                        Builders<ProjectionOutboxDocument>.Filter.Lte(item => item.NextPublishAttemptAtUtc, now)),
                    Builders<ProjectionOutboxDocument>.Filter.And(
                        Builders<ProjectionOutboxDocument>.Filter.Eq(item => item.State, ProjectionOutboxState.Publishing),
                        Builders<ProjectionOutboxDocument>.Filter.Lte(item => item.LockExpiresAtUtc, now))),
                Builders<ProjectionOutboxDocument>.Filter.Lt(
                    item => item.PublishAttemptCount, _options.MaximumPublishAttempts));
            var update = Builders<ProjectionOutboxDocument>.Update
                .Set(item => item.State, ProjectionOutboxState.Publishing)
                .Set(item => item.LockedBy, lockOwner)
                .Set(item => item.LockExpiresAtUtc, now.Add(lockDuration))
                .Inc(item => item.PublishAttemptCount, 1);
            var document = await _collection.FindOneAndUpdateAsync(filter, update,
                new FindOneAndUpdateOptions<ProjectionOutboxDocument, ProjectionOutboxDocument>
                {
                    Sort = Builders<ProjectionOutboxDocument>.Sort.Ascending(item => item.CreatedAtUtc),
                    ReturnDocument = ReturnDocument.After
                }, cancellationToken).ConfigureAwait(false);
            if (document is null) break;
            claimed.Add(document.ToModel());
        }
        return claimed;
    }

    public Task MarkPublishedAsync(string eventId, string lockOwner, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var update = Builders<ProjectionOutboxDocument>.Update
            .Set(item => item.State, ProjectionOutboxState.Published)
            .Set(item => item.PublishedAtUtc, now.UtcDateTime)
            .Set(item => item.LockedBy, null)
            .Set(item => item.LockExpiresAtUtc, null)
            .Set(item => item.LastErrorCode, null);
        return _collection.UpdateOneAsync(
            item => item.Id == eventId && item.State == ProjectionOutboxState.Publishing && item.LockedBy == lockOwner,
            update, cancellationToken: cancellationToken);
    }

    public Task ReleaseAsync(
        string eventId,
        string lockOwner,
        DateTimeOffset nextAttemptAtUtc,
        string errorCode,
        bool terminal,
        CancellationToken cancellationToken = default)
    {
        var update = Builders<ProjectionOutboxDocument>.Update
            .Set(item => item.State, terminal ? ProjectionOutboxState.Failed : ProjectionOutboxState.Pending)
            .Set(item => item.NextPublishAttemptAtUtc, nextAttemptAtUtc.ToUniversalTime())
            .Set(item => item.LockedBy, null)
            .Set(item => item.LockExpiresAtUtc, null)
            .Set(item => item.LastErrorCode, SafeCode(errorCode));
        return _collection.UpdateOneAsync(
            item => item.Id == eventId && item.State == ProjectionOutboxState.Publishing && item.LockedBy == lockOwner,
            update, cancellationToken: cancellationToken);
    }

    public async Task<ProjectionOutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken = default)
    {
        var pending = Builders<ProjectionOutboxDocument>.Filter.In(item => item.State,
            [ProjectionOutboxState.Pending, ProjectionOutboxState.Publishing]);
        var count = await _collection.CountDocumentsAsync(pending, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var oldest = await _collection.Find(pending).SortBy(item => item.CreatedAtUtc)
            .Project(item => (DateTimeOffset?)item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return new(count, oldest);
    }

    private static string SafeCode(string value) => value.Length <= 128 ? value : value[..128];

    internal sealed class ProjectionOutboxDocument
    {
        [BsonId] public required string Id { get; init; }
        public required string EventType { get; init; }
        public required string SchemaVersion { get; init; }
        public DateTimeOffset OccurredAtUtc { get; init; }
        public required string PayloadJson { get; init; }
        public ProjectionOutboxState State { get; set; }
        public int PublishAttemptCount { get; set; }
        public DateTimeOffset NextPublishAttemptAtUtc { get; set; }
        public string? LockedBy { get; set; }
        public DateTimeOffset? LockExpiresAtUtc { get; set; }
        public DateTime? PublishedAtUtc { get; set; }
        public string? LastErrorCode { get; set; }
        public DateTimeOffset CreatedAtUtc { get; init; }

        public ProjectionOutboxEntry ToModel() => new(
            JsonSerializer.Deserialize<EmailValidationObservationEnvelope>(PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException($"Projection outbox event '{Id}' is malformed."),
            State, PublishAttemptCount, NextPublishAttemptAtUtc, LockedBy, LockExpiresAtUtc,
            PublishedAtUtc is { } published
                ? new DateTimeOffset(DateTime.SpecifyKind(published, DateTimeKind.Utc)) : null,
            LastErrorCode, CreatedAtUtc);
    }
}

public sealed class ProjectionValidationLifecycleStore(
    MongoValidationLifecycleStore inner,
    IObservationEventFactory eventFactory,
    IProjectionOutbox outbox,
    IOptions<EmailValidationOptions> options,
    ILogger<ProjectionValidationLifecycleStore> logger) : IValidationLifecycleStore, IRevalidationOutbox
{
    private readonly bool _enabled = options.Value.Projection.Enabled;

    public Task<ValidationLifecycle?> GetAsync(string validationId, CancellationToken cancellationToken = default) =>
        inner.GetAsync(validationId, cancellationToken);

    public Task<ValidationLifecycle?> GetActiveByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        inner.GetActiveByEmailAsync(normalizedEmail, cancellationToken);

    public async Task<LifecycleWriteResult> TrySaveAsync(
        ValidationLifecycle lifecycle,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var previous = _enabled && expectedVersion > 0
            ? await inner.GetAsync(lifecycle.ValidationId, cancellationToken).ConfigureAwait(false)
            : null;
        var saved = await inner.TrySaveAsync(lifecycle, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (!_enabled || !saved.Applied || saved.Lifecycle is null) return saved;
        try
        {
            var events = await eventFactory.CreateLifecycleEventsAsync(saved.Lifecycle, previous, cancellationToken)
                .ConfigureAwait(false);
            foreach (var observation in events)
                await outbox.EnqueueAsync(observation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "Canonical lifecycle {ValidationId} sequence {Sequence} persisted but projection outbox creation failed; reconciliation will repair it",
                saved.Lifecycle.ValidationId, saved.Lifecycle.Sequence);
        }
        return saved;
    }

    public Task<PendingRevalidation?> TryClaimAsync(string validationId, TimeSpan lease,
        CancellationToken cancellationToken = default) => inner.TryClaimAsync(validationId, lease, cancellationToken);

    public Task<IReadOnlyList<string>> GetPendingValidationIdsAsync(int maximumCount,
        CancellationToken cancellationToken = default) => inner.GetPendingValidationIdsAsync(maximumCount, cancellationToken);

    public async Task<bool> MarkScheduledAsync(string validationId, string messageId,
        RevalidationScheduleResult result, CancellationToken cancellationToken = default)
    {
        var previous = _enabled ? await inner.GetAsync(validationId, cancellationToken).ConfigureAwait(false) : null;
        var applied = await inner.MarkScheduledAsync(validationId, messageId, result, cancellationToken)
            .ConfigureAwait(false);
        if (!applied || !_enabled) return applied;
        var current = await inner.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
        if (current is not null) await EnqueueBestEffortAsync(current, previous, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task ReleaseAsync(string validationId, string messageId, string? errorCode,
        CancellationToken cancellationToken = default) => inner.ReleaseAsync(validationId, messageId, errorCode, cancellationToken);

    private async Task EnqueueBestEffortAsync(
        ValidationLifecycle lifecycle,
        ValidationLifecycle? previous,
        CancellationToken cancellationToken)
    {
        try
        {
            var events = await eventFactory.CreateLifecycleEventsAsync(lifecycle, previous, cancellationToken)
                .ConfigureAwait(false);
            foreach (var observation in events)
                await outbox.EnqueueAsync(observation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "Canonical lifecycle {ValidationId} sequence {Sequence} persisted but projection outbox creation failed; reconciliation will repair it",
                lifecycle.ValidationId, lifecycle.Sequence);
        }
    }
}

public sealed class ProjectionOutboundIdentityHealthStore(
    MongoOutboundIdentityHealthStore inner,
    IObservationEventFactory eventFactory,
    IProjectionOutbox outbox,
    IOptions<EmailValidationOptions> options,
    ILogger<ProjectionOutboundIdentityHealthStore> logger) : IOutboundIdentityHealthStore
{
    private readonly bool _enabled = options.Value.Projection.Enabled;

    public Task<OutboundIdentityHealth> GetAsync(string identityId, MailProvider provider,
        CancellationToken cancellationToken = default) => inner.GetAsync(identityId, provider, cancellationToken);

    public async Task RecordAsync(OutboundIdentityOutcome outcome, CancellationToken cancellationToken = default)
    {
        var provider = !outcome.Global &&
            outcome.CooldownScope is SmtpCooldownScope.OutboundIdentity or SmtpCooldownScope.SourceIp
                ? outcome.Provider : MailProvider.Unknown;
        var previous = await inner.GetAsync(outcome.IdentityId, provider, cancellationToken).ConfigureAwait(false);
        await inner.RecordAsync(outcome, cancellationToken).ConfigureAwait(false);
        if (!_enabled) return;
        var current = await inner.GetAsync(outcome.IdentityId, provider, cancellationToken).ConfigureAwait(false);
        var observation = eventFactory.CreateOutboundHealthEvent(previous, current, outcome.ObservedAtUtc);
        if (observation is null) return;
        try
        {
            await outbox.EnqueueAsync(observation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "Canonical outbound identity health for {IdentityId} persisted but projection outbox creation failed",
                outcome.IdentityId);
        }
    }
}

public sealed class MongoProjectionReconciler : IProjectionReconciler, IProjectionPersistenceInitializer
{
    private readonly IMongoCollection<MongoValidationLifecycleStore.ValidationLifecycleDocument> _lifecycles;
    private readonly IMongoCollection<ProjectionCheckpointDocument> _checkpoints;
    private readonly IObservationEventFactory _eventFactory;
    private readonly IProjectionOutbox _outbox;
    private readonly ProjectionReconciliationOptions _options;
    private readonly TimeProvider _timeProvider;

    public MongoProjectionReconciler(
        IMongoClient client,
        IOptions<EmailValidationOptions> options,
        IObservationEventFactory eventFactory,
        IProjectionOutbox outbox,
        TimeProvider timeProvider)
    {
        var configured = options.Value;
        var database = client.GetDatabase(configured.Persistence.DatabaseName);
        _lifecycles = database.GetCollection<MongoValidationLifecycleStore.ValidationLifecycleDocument>(
            configured.Persistence.LifecycleCollection);
        _checkpoints = database.GetCollection<ProjectionCheckpointDocument>(
            configured.Projection.Outbox.CheckpointCollectionName);
        _eventFactory = eventFactory;
        _outbox = outbox;
        _options = configured.Projection.Reconciliation;
        _timeProvider = timeProvider;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _checkpoints.Indexes.CreateOneAsync(new CreateIndexModel<ProjectionCheckpointDocument>(
            Builders<ProjectionCheckpointDocument>.IndexKeys.Ascending(item => item.UpdatedAtUtc),
            new CreateIndexOptions { Name = "ix_projection_checkpoint_updated" }), cancellationToken: cancellationToken);

    public async Task<ProjectionReplayResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        const string name = "email-validation-observations-v1";
        var checkpoint = await _checkpoints.Find(item => item.Id == name)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var to = _timeProvider.GetUtcNow();
        var from = (checkpoint?.LastObservedTimestamp ?? to)
            .Subtract(TimeSpan.FromMinutes(_options.OverlapMinutes));
        var result = await BackfillCoreAsync(new ProjectionReplayRequest(
            from, to, _options.BatchSize, _options.MaximumEventsPerRun), cancellationToken).ConfigureAwait(false);
        if (result.LastObservedAtUtc is not null)
        {
            await _checkpoints.ReplaceOneAsync(item => item.Id == name, new ProjectionCheckpointDocument
            {
                Id = name,
                LastObservedTimestamp = result.LastObservedAtUtc.Value,
                LastStableIdentifier = result.LastStableIdentifier,
                UpdatedAtUtc = _timeProvider.GetUtcNow()
            }, new ReplaceOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    public Task<ProjectionReplayResult> BackfillAsync(
        ProjectionReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.FromUtc >= request.ToUtc) throw new ArgumentException("Backfill FromUtc must precede ToUtc.");
        if (request.TenantId is not null)
            throw new NotSupportedException("Canonical validation lifecycle records do not currently carry tenant identity.");
        return BackfillCoreAsync(request, cancellationToken);
    }

    private async Task<ProjectionReplayResult> BackfillCoreAsync(
        ProjectionReplayRequest request,
        CancellationToken cancellationToken)
    {
        var from = request.FromUtc.ToUniversalTime();
        var to = request.ToUtc.ToUniversalTime();
        var considered = 0;
        var enqueued = 0;
        var recordsRead = 0;
        DateTimeOffset? lastAt = null;
        string? lastId = null;
        var cursorAt = from;
        var cursorId = string.Empty;
        while (considered < request.MaximumEvents)
        {
            var range = Builders<MongoValidationLifecycleStore.ValidationLifecycleDocument>.Filter.And(
                Builders<MongoValidationLifecycleStore.ValidationLifecycleDocument>.Filter.Gte(
                    item => item.UpdatedAt, from),
                Builders<MongoValidationLifecycleStore.ValidationLifecycleDocument>.Filter.Lte(
                    item => item.UpdatedAt, to));
            var afterCursor = Builders<MongoValidationLifecycleStore.ValidationLifecycleDocument>.Filter.Or(
                Builders<MongoValidationLifecycleStore.ValidationLifecycleDocument>.Filter.Gt(
                    item => item.UpdatedAt, cursorAt),
                Builders<MongoValidationLifecycleStore.ValidationLifecycleDocument>.Filter.And(
                    Builders<MongoValidationLifecycleStore.ValidationLifecycleDocument>.Filter.Eq(
                        item => item.UpdatedAt, cursorAt),
                    Builders<MongoValidationLifecycleStore.ValidationLifecycleDocument>.Filter.Gt(
                        item => item.Id, cursorId)));
            var pageSize = Math.Max(1, Math.Min(request.BatchSize, request.MaximumEvents - considered));
            var documents = await _lifecycles.Find(
                    Builders<MongoValidationLifecycleStore.ValidationLifecycleDocument>.Filter.And(range, afterCursor))
                .SortBy(item => item.UpdatedAt).ThenBy(item => item.Id)
                .Limit(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (documents.Count == 0) break;
            foreach (var document in documents)
            {
                recordsRead++;
                var events = await _eventFactory.CreateLifecycleEventsAsync(
                    document.ToModel(), null, cancellationToken).ConfigureAwait(false);
                foreach (var observation in events)
                {
                    if (request.EventType is not null &&
                        !string.Equals(request.EventType, observation.EventType, StringComparison.Ordinal))
                        continue;
                    if (considered >= request.MaximumEvents) break;
                    considered++;
                    if (!request.DryRun && await _outbox.EnqueueAsync(observation, cancellationToken).ConfigureAwait(false))
                        enqueued++;
                }
                lastAt = document.UpdatedAt;
                lastId = document.Id;
                cursorAt = document.UpdatedAt;
                cursorId = document.Id;
                if (considered >= request.MaximumEvents) break;
            }
            if (documents.Count < pageSize) break;
        }
        return new(recordsRead, considered, enqueued, lastAt, lastId, request.DryRun);
    }

    internal sealed class ProjectionCheckpointDocument
    {
        [BsonId] public required string Id { get; init; }
        public DateTimeOffset LastObservedTimestamp { get; init; }
        public string? LastStableIdentifier { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; init; }
    }
}

public static class ObservationEventSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(EmailValidationObservationEnvelope observation) =>
        JsonSerializer.SerializeToUtf8Bytes(observation, Options);

    public static bool TryDeserialize(
        ReadOnlyMemory<byte> body,
        out EmailValidationObservationEnvelope? observation,
        out string? reason)
    {
        try
        {
            observation = JsonSerializer.Deserialize<EmailValidationObservationEnvelope>(body.Span, Options);
            if (observation is null || string.IsNullOrWhiteSpace(observation.EventId) ||
                string.IsNullOrWhiteSpace(observation.EventType) || observation.OccurredAtUtc == default ||
                observation.Payload.ValueKind != JsonValueKind.Object)
            {
                observation = null;
                reason = "missing_required_field";
                return false;
            }
            if (observation.SchemaVersion != EmailValidationObservationTypes.SchemaVersionV1 ||
                observation.EventType is not (EmailValidationObservationTypes.AttemptV1 or
                    EmailValidationObservationTypes.LifecycleV1 or
                    EmailValidationObservationTypes.OutboundIdentityHealthV1))
            {
                observation = null;
                reason = "unsupported_event_schema";
                return false;
            }
            reason = null;
            return true;
        }
        catch (JsonException)
        {
            observation = null;
            reason = "malformed_json";
            return false;
        }
    }
}

public interface IProjectionOutboxDispatcher
{
    Task<int> DispatchAsync(CancellationToken cancellationToken = default);
}

public sealed class ProjectionOutboxDispatcher(
    IProjectionOutbox outbox,
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider,
    ILogger<ProjectionOutboxDispatcher> logger) : IProjectionOutboxDispatcher, IAsyncDisposable
{
    private readonly EmailValidationProjectionOptions _options = options.Value.Projection;
    private ServiceBusClient? _client;
    private ServiceBusSender? _sender;

    public async Task<int> DispatchAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return 0;
        var owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        var entries = await outbox.ClaimAsync(_options.Outbox.BatchSize, owner,
            TimeSpan.FromSeconds(_options.Outbox.LockDurationSeconds), cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0) return 0;
        try
        {
            _client ??= new ServiceBusClient(_options.ServiceBus.ConnectionString);
            _sender ??= _client.CreateSender(_options.ServiceBus.TopicName);
            var published = 0;
            var index = 0;
            while (index < entries.Count)
            {
                using var batch = await _sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);
                var included = new List<ProjectionOutboxEntry>();
                while (index < entries.Count)
                {
                    var entry = entries[index];
                    if (batch.TryAddMessage(ToMessage(entry.Event)))
                    {
                        included.Add(entry);
                        index++;
                        continue;
                    }
                    if (included.Count > 0) break;
                    await outbox.ReleaseAsync(entry.Event.EventId, owner, timeProvider.GetUtcNow(),
                        "message_too_large", true, cancellationToken).ConfigureAwait(false);
                    ProjectionTelemetry.PublishFailure(entry.Event.EventType, "message_too_large");
                    index++;
                }
                if (included.Count == 0) continue;
                await _sender.SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
                foreach (var entry in included)
                {
                    await outbox.MarkPublishedAsync(entry.Event.EventId, owner, cancellationToken).ConfigureAwait(false);
                    ProjectionTelemetry.PublishSuccess(entry.Event.EventType);
                    published++;
                }
            }
            return published;
        }
        catch (Exception exception) when (exception is ServiceBusException or TimeoutException)
        {
            var retryAt = timeProvider.GetUtcNow().AddSeconds(Math.Min(300,
                Math.Pow(2, Math.Min(8, entries.Max(item => item.PublishAttemptCount)))));
            foreach (var entry in entries)
            {
                await outbox.ReleaseAsync(entry.Event.EventId, owner, retryAt,
                    exception.GetType().Name, false, cancellationToken).ConfigureAwait(false);
                ProjectionTelemetry.PublishFailure(entry.Event.EventType, "service_bus_transient");
            }
            logger.LogWarning(exception,
                "Observation topic publication failed; {Count} events remain in the Mongo outbox", entries.Count);
            return 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync().ConfigureAwait(false);
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
    }

    internal static ServiceBusMessage ToMessage(EmailValidationObservationEnvelope observation)
    {
        var message = new ServiceBusMessage(BinaryData.FromBytes(ObservationEventSerializer.Serialize(observation)))
        {
            MessageId = observation.EventId,
            CorrelationId = observation.ValidationId,
            Subject = observation.EventType,
            ContentType = "application/json"
        };
        message.ApplicationProperties["schemaVersion"] = observation.SchemaVersion;
        message.ApplicationProperties["eventType"] = observation.EventType;
        message.ApplicationProperties["environment"] = observation.Environment;
        if (observation.TenantId is not null) message.ApplicationProperties["tenantId"] = observation.TenantId;
        return message;
    }
}

public enum ProjectionIndexDisposition { Indexed, Duplicate, Retryable, PermanentFailure }

public sealed record ProjectionIndexResult(
    string EventId,
    ProjectionIndexDisposition Disposition,
    int? StatusCode = null,
    string? FailureCategory = null);

public interface IElasticsearchObservationSink
{
    Task<IReadOnlyList<ProjectionIndexResult>> IndexBatchAsync(
        IReadOnlyList<EmailValidationObservationEnvelope> observations,
        CancellationToken cancellationToken = default);
}

public sealed class ElasticsearchObservationSink(
    HttpClient httpClient,
    IOptions<EmailValidationOptions> options) : IElasticsearchObservationSink
{
    private readonly ProjectionElasticsearchOptions _options = options.Value.Projection.Elasticsearch;

    public async Task<IReadOnlyList<ProjectionIndexResult>> IndexBatchAsync(
        IReadOnlyList<EmailValidationObservationEnvelope> observations,
        CancellationToken cancellationToken = default)
    {
        if (observations.Count == 0) return [];
        var body = BuildBulkBody(observations);
        if (Encoding.UTF8.GetByteCount(body) > _options.MaximumBatchBytes)
        {
            if (observations.Count == 1)
                return [new ProjectionIndexResult(observations[0].EventId,
                    ProjectionIndexDisposition.PermanentFailure, 413, "document_too_large")];
            var middle = observations.Count / 2;
            var left = await IndexBatchAsync(observations.Take(middle).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            var right = await IndexBatchAsync(observations.Skip(middle).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            return left.Concat(right).ToArray();
        }
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.DataStreamName}/_bulk?refresh=false")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson")
        };
        ApplyAuthentication(request);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or TaskCanceledException &&
            !cancellationToken.IsCancellationRequested)
        {
            return observations.Select(item => new ProjectionIndexResult(item.EventId,
                ProjectionIndexDisposition.Retryable, null, "connection_failure")).ToArray();
        }
        using (response)
        {
            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                return observations.Select(item => new ProjectionIndexResult(item.EventId,
                    ProjectionIndexDisposition.Retryable, (int)response.StatusCode, "elasticsearch_transient"))
                    .ToArray();
            if (!response.IsSuccessStatusCode)
                return observations.Select(item => new ProjectionIndexResult(item.EventId,
                    ProjectionIndexDisposition.PermanentFailure, (int)response.StatusCode,
                    "elasticsearch_request_rejected")).ToArray();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return ParseBulkResponse(observations, json.RootElement);
        }
    }

    internal static IReadOnlyList<ProjectionIndexResult> ParseBulkResponse(
        IReadOnlyList<EmailValidationObservationEnvelope> observations,
        JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() != observations.Count)
            return observations.Select(item => new ProjectionIndexResult(item.EventId,
                ProjectionIndexDisposition.Retryable, null, "invalid_bulk_response")).ToArray();
        var results = new List<ProjectionIndexResult>(observations.Count);
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var operation = item.GetProperty("create");
            var status = operation.GetProperty("status").GetInt32();
            var disposition = status switch
            {
                >= 200 and < 300 => ProjectionIndexDisposition.Indexed,
                409 => ProjectionIndexDisposition.Duplicate,
                429 or >= 500 => ProjectionIndexDisposition.Retryable,
                _ => ProjectionIndexDisposition.PermanentFailure
            };
            var category = operation.TryGetProperty("error", out var error) &&
                error.TryGetProperty("type", out var type) ? type.GetString() : null;
            results.Add(new(observations[index++].EventId, disposition, status, category));
        }
        return results;
    }

    internal static string BuildBulkBody(IReadOnlyList<EmailValidationObservationEnvelope> observations)
    {
        var builder = new StringBuilder();
        foreach (var observation in observations)
        {
            builder.Append(JsonSerializer.Serialize(new { create = new { _id = observation.EventId } }));
            builder.Append('\n');
            builder.Append(BuildDocument(observation).ToJsonString());
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static JsonObject BuildDocument(EmailValidationObservationEnvelope observation)
    {
        var document = new JsonObject
        {
            ["@timestamp"] = observation.OccurredAtUtc,
            ["recordedAtUtc"] = observation.RecordedAtUtc,
            ["eventId"] = observation.EventId,
            ["eventType"] = observation.EventType,
            ["schemaVersion"] = observation.SchemaVersion,
            ["mappingVersion"] = EmailValidationObservationTypes.MappingVersionV1,
            ["environment"] = observation.Environment
        };
        Add(document, "tenantId", observation.TenantId);
        Add(document, "consumerId", observation.ConsumerId);
        Add(document, "validationId", observation.ValidationId);
        Add(document, "jobId", observation.JobId);
        if (observation.Sequence.HasValue) document["sequence"] = observation.Sequence.Value;
        foreach (var property in observation.Payload.EnumerateObject())
            document[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return document;
    }

    private void ApplyAuthentication(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", _options.ApiKey);
        else if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
        }
    }

    private static void Add(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[name] = value;
    }
}

public static class ProjectionTelemetry
{
    private static readonly Meter Meter = new("EmailValidation.Projection", "1.0.0");
    private static readonly Counter<long> Created = Meter.CreateCounter<long>("email_validation_observation_created_total");
    private static readonly Counter<long> PublishSucceeded = Meter.CreateCounter<long>("email_validation_outbox_publish_success_total");
    private static readonly Counter<long> PublishFailed = Meter.CreateCounter<long>("email_validation_outbox_publish_failure_total");
    internal static readonly Counter<long> Received = Meter.CreateCounter<long>("email_validation_projection_received_total");
    internal static readonly Counter<long> Indexed = Meter.CreateCounter<long>("email_validation_projection_indexed_total");
    internal static readonly Counter<long> Duplicate = Meter.CreateCounter<long>("email_validation_projection_duplicate_total");
    internal static readonly Counter<long> Retried = Meter.CreateCounter<long>("email_validation_projection_retry_total");
    internal static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("email_validation_projection_dead_letter_total");
    internal static readonly Histogram<double> BulkDuration = Meter.CreateHistogram<double>("email_validation_projection_bulk_duration", "ms");
    internal static readonly Histogram<double> Lag = Meter.CreateHistogram<double>("email_validation_projection_lag_seconds", "s");
    private static readonly Counter<long> MappingFailures = Meter.CreateCounter<long>("email_validation_projection_mapping_failure_total");
    private static readonly Counter<long> Reconciled = Meter.CreateCounter<long>("email_validation_projection_reconciliation_created_total");
    private static readonly Counter<long> BackfillProgress = Meter.CreateCounter<long>("email_validation_projection_backfill_progress");
    private static long _pending;
    private static double _oldestAgeSeconds;
    private static readonly ObservableGauge<long> PendingGauge = Meter.CreateObservableGauge(
        "email_validation_outbox_pending", () => Volatile.Read(ref _pending));
    private static readonly ObservableGauge<double> OldestGauge = Meter.CreateObservableGauge(
        "email_validation_outbox_oldest_age_seconds", () => Volatile.Read(ref _oldestAgeSeconds), "s");

    internal static void ObservationCreated(string eventType, string schemaVersion) => Created.Add(1,
        new("event_type", eventType), new("schema_version", schemaVersion));
    internal static void PublishSuccess(string eventType) => PublishSucceeded.Add(1,
        new KeyValuePair<string, object?>("event_type", eventType));
    internal static void PublishFailure(string eventType, string category) => PublishFailed.Add(1,
        new("event_type", eventType), new("failure_category", category));

    public static void RecordReceived(string eventType) => Received.Add(1,
        new KeyValuePair<string, object?>("event_type", eventType));
    public static void RecordIndexed(string eventType) => Indexed.Add(1,
        new KeyValuePair<string, object?>("event_type", eventType));
    public static void RecordDuplicate(string eventType) => Duplicate.Add(1,
        new KeyValuePair<string, object?>("event_type", eventType));
    public static void RecordRetry(string eventType, string? category) => Retried.Add(1,
        new("event_type", eventType), new("failure_category", category ?? "unknown"));
    public static void RecordDeadLetter(string eventType, string? category) => DeadLettered.Add(1,
        new("event_type", eventType), new("failure_category", category ?? "unknown"));
    public static void RecordMappingFailure(string eventType) => MappingFailures.Add(1,
        new KeyValuePair<string, object?>("event_type", eventType));
    public static void RecordBulkDuration(TimeSpan duration) => BulkDuration.Record(duration.TotalMilliseconds);
    public static void RecordLag(EmailValidationObservationEnvelope observation, DateTimeOffset projectedAtUtc) =>
        Lag.Record(Math.Max(0, (projectedAtUtc - observation.OccurredAtUtc).TotalSeconds),
            new KeyValuePair<string, object?>("event_type", observation.EventType));
    public static void ObserveBacklog(ProjectionOutboxBacklog backlog, DateTimeOffset now)
    {
        Volatile.Write(ref _pending, backlog.PendingCount);
        Volatile.Write(ref _oldestAgeSeconds, backlog.OldestCreatedAtUtc is null
            ? 0 : Math.Max(0, (now - backlog.OldestCreatedAtUtc.Value).TotalSeconds));
    }
    public static void RecordReconciled(int count)
    {
        if (count > 0) Reconciled.Add(count);
    }
    public static void RecordBackfillProgress(int count, bool dryRun) => BackfillProgress.Add(count,
        new KeyValuePair<string, object?>("dry_run", dryRun));
}
