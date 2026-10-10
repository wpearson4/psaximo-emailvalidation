using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public sealed class InMemoryValidationStatusDispatcher :
    IValidationStatusPublisher,
    IValidationStatusSubscription,
    IDisposable
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly IValidationStatusQueryService _query;
    private readonly ILogger<InMemoryValidationStatusDispatcher> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Meter _meter = new("EmailValidation.Status");
    private readonly Counter<long> _eventsPublished;
    private readonly Counter<long> _eventsDelivered;
    private readonly Counter<long> _subscriptions;

    public InMemoryValidationStatusDispatcher(
        IValidationStatusQueryService query,
        ILogger<InMemoryValidationStatusDispatcher> logger,
        TimeProvider? timeProvider = null)
    {
        _query = query;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _eventsPublished = _meter.CreateCounter<long>("email_validation.status.events_published");
        _eventsDelivered = _meter.CreateCounter<long>("email_validation.status.events_delivered");
        _subscriptions = _meter.CreateCounter<long>("email_validation.status.subscriptions");
    }

    public Task PublishAsync(ValidationStatusChanged status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _eventsPublished.Add(1, new KeyValuePair<string, object?>("lifecycle_state", status.LifecycleState.ToString()));
        foreach (var subscriber in _subscribers.Values)
        {
            if (!string.Equals(subscriber.ValidationId, status.ValidationId, StringComparison.Ordinal) ||
                status.Sequence <= Volatile.Read(ref subscriber.LastSequence))
                continue;
            if (subscriber.Channel.Writer.TryWrite(status))
                _eventsDelivered.Add(1);
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ValidationStatusChanged> SubscribeAsync(
        string validationId,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationId);
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ValidationStatusChanged>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        var subscriber = new Subscriber(validationId, channel, afterSequence);
        _subscribers[id] = subscriber;
        _subscriptions.Add(1, new KeyValuePair<string, object?>("event", "opened"));
        _logger.LogInformation("Status subscriber connected for validation {ValidationId}", validationId);
        try
        {
            var snapshot = await _query.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null && snapshot.Sequence > afterSequence)
            {
                var initial = ValidationStatusMapper.ToEvent(snapshot, _timeProvider.GetUtcNow());
                Volatile.Write(ref subscriber.LastSequence, initial.Sequence);
                yield return initial;
                if (initial.LifecycleState is ValidationLifecycleState.Final or ValidationLifecycleState.Failed)
                    yield break;
            }
            else if (snapshot?.LifecycleState is ValidationLifecycleState.Final or ValidationLifecycleState.Failed)
            {
                yield break;
            }

            await foreach (var status in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var last = Volatile.Read(ref subscriber.LastSequence);
                if (status.Sequence <= last) continue;
                Volatile.Write(ref subscriber.LastSequence, status.Sequence);
                yield return status;
                if (status.LifecycleState is ValidationLifecycleState.Final or ValidationLifecycleState.Failed)
                    yield break;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
            _subscriptions.Add(1, new KeyValuePair<string, object?>("event", "closed"));
            _logger.LogInformation("Status subscriber disconnected for validation {ValidationId}", validationId);
        }
    }

    public void Dispose() => _meter.Dispose();

    private sealed class Subscriber(
        string validationId,
        Channel<ValidationStatusChanged> channel,
        long lastSequence)
    {
        public string ValidationId { get; } = validationId;
        public Channel<ValidationStatusChanged> Channel { get; } = channel;
        public long LastSequence = lastSequence;
    }
}

public sealed class InMemoryValidationLifecycleStore(TimeProvider? timeProvider = null) : IValidationLifecycleStore
{
    private readonly object _executionSync = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ValidationLifecycle> _lifecycles = new(StringComparer.Ordinal);

    public Task<ValidationLifecycle?> GetAsync(string validationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lifecycles.TryGetValue(validationId, out var lifecycle);
        return Task.FromResult(lifecycle);
    }

    public Task<ValidationLifecycle?> GetActiveByEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lifecycle = _lifecycles.Values
            .Where(item => item.ResultState == ValidationResultState.Provisional &&
                string.Equals(item.MailboxKey, MailboxIdentity.TryCreate(normalizedEmail)?.Key ?? normalizedEmail, StringComparison.Ordinal))
            .OrderByDescending(item => item.LastUpdatedAt)
            .FirstOrDefault();
        return Task.FromResult(lifecycle);
    }

    public Task<ValidationLifecycle?> TryAcquireExecutionAsync(string validationId, long expectedVersion,
        int attemptNumber, string ownerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_executionSync)
        {
            if (!_lifecycles.TryGetValue(validationId, out var current) || current.Version != expectedVersion)
                return Task.FromResult<ValidationLifecycle?>(null);
            var acquired = RevalidationExecution.Acquire(current, attemptNumber, ownerId, lease, _clock.GetUtcNow());
            if (acquired is not null) _lifecycles[validationId] = acquired;
            return Task.FromResult(acquired);
        }
    }

    public Task<bool> RenewExecutionAsync(string validationId, RevalidationExecutionLease lease,
        TimeSpan duration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_executionSync)
        {
            if (!_lifecycles.TryGetValue(validationId, out var current) || !RevalidationExecution.Owns(current, lease, _clock.GetUtcNow()))
                return Task.FromResult(false);
            _lifecycles[validationId] = current with { ExecutionLease = lease with { ExpiresAt = _clock.GetUtcNow().Add(duration) } };
            return Task.FromResult(true);
        }
    }

    public Task<LifecycleWriteResult> TrySaveExecutionAsync(ValidationLifecycle lifecycle, long expectedVersion,
        RevalidationExecutionLease lease, CancellationToken cancellationToken = default) =>
        Save(lifecycle, expectedVersion, lease, cancellationToken);

    public Task<LifecycleWriteResult> TrySaveAsync(ValidationLifecycle lifecycle, long expectedVersion,
        CancellationToken cancellationToken = default) => Save(lifecycle, expectedVersion, null, cancellationToken);

    private Task<LifecycleWriteResult> Save(ValidationLifecycle lifecycle, long expectedVersion,
        RevalidationExecutionLease? lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_executionSync)
        {
            _lifecycles.TryGetValue(lifecycle.ValidationId, out var current);
            if (lease is not null ? current is null || !RevalidationExecution.Owns(current, lease, _clock.GetUtcNow()) : current?.ExecutionLease is not null)
                return Task.FromResult(new LifecycleWriteResult(false, null));
            var applied = expectedVersion == 0
                ? _lifecycles.TryAdd(lifecycle.ValidationId, lifecycle)
                : current is not null && current.Version == expectedVersion && _lifecycles.TryUpdate(lifecycle.ValidationId, lifecycle, current);
            return Task.FromResult(new LifecycleWriteResult(applied, applied ? lifecycle : null));
        }
    }
}

public sealed class MongoValidationStatusSubscription : IValidationStatusSubscription, IDisposable
{
    private readonly IMongoCollection<MongoValidationLifecycleStore.ValidationLifecycleDocument> _collection;
    private readonly IValidationStatusQueryService _query;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MongoValidationStatusSubscription> _logger;
    private readonly Meter _meter = new("EmailValidation.Status.Mongo");
    private readonly Counter<long> _subscriptions;
    private readonly Counter<long> _eventsDelivered;

    public MongoValidationStatusSubscription(
        IMongoClient client,
        IOptions<EmailValidationOptions> options,
        IValidationStatusQueryService query,
        TimeProvider timeProvider,
        ILogger<MongoValidationStatusSubscription> logger)
    {
        var persistence = options.Value.Persistence;
        _collection = client.GetDatabase(persistence.DatabaseName)
            .GetCollection<MongoValidationLifecycleStore.ValidationLifecycleDocument>(persistence.LifecycleCollection);
        _query = query;
        _timeProvider = timeProvider;
        _logger = logger;
        _subscriptions = _meter.CreateCounter<long>("email_validation.status.mongo_subscriptions");
        _eventsDelivered = _meter.CreateCounter<long>("email_validation.status.mongo_events_delivered");
    }

    public async IAsyncEnumerable<ValidationStatusChanged> SubscribeAsync(
        string validationId,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationId);
        var pipeline = new EmptyPipelineDefinition<
                ChangeStreamDocument<MongoValidationLifecycleStore.ValidationLifecycleDocument>>()
            .Match(change => change.FullDocument.Id == validationId);
        var options = new ChangeStreamOptions
        {
            FullDocument = ChangeStreamFullDocumentOption.UpdateLookup,
            MaxAwaitTime = TimeSpan.FromSeconds(15)
        };
        using var cursor = await _collection.WatchAsync(pipeline, options, cancellationToken).ConfigureAwait(false);
        _subscriptions.Add(1, new KeyValuePair<string, object?>("event", "opened"));
        _logger.LogInformation("Mongo status subscription opened for validation {ValidationId}", validationId);
        var lastSequence = afterSequence;
        try
        {
            var snapshot = await _query.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null && snapshot.Sequence > lastSequence)
            {
                var initial = ValidationStatusMapper.ToEvent(snapshot, _timeProvider.GetUtcNow());
                lastSequence = initial.Sequence;
                yield return initial;
                if (initial.LifecycleState is ValidationLifecycleState.Final or ValidationLifecycleState.Failed)
                    yield break;
            }
            else if (snapshot?.LifecycleState is ValidationLifecycleState.Final or ValidationLifecycleState.Failed)
            {
                yield break;
            }

            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var change in cursor.Current)
                {
                    if (change.FullDocument is null) continue;
                    var status = ValidationStatusMapper.ToEvent(
                        change.FullDocument.ToModel(), _timeProvider.GetUtcNow());
                    if (status.Sequence <= lastSequence) continue;
                    lastSequence = status.Sequence;
                    _eventsDelivered.Add(1,
                        new KeyValuePair<string, object?>("lifecycle_state", status.LifecycleState.ToString()));
                    yield return status;
                    if (status.LifecycleState is ValidationLifecycleState.Final or ValidationLifecycleState.Failed)
                        yield break;
                }
            }
        }
        finally
        {
            _subscriptions.Add(1, new KeyValuePair<string, object?>("event", "closed"));
            _logger.LogInformation("Mongo status subscription closed for validation {ValidationId}", validationId);
        }
    }

    public void Dispose() => _meter.Dispose();

}
