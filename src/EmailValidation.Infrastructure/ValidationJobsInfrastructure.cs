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

public sealed class MongoValidationJobStore : IValidationJobStore
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
        await _items.Indexes.CreateOneAsync(new CreateIndexModel<ItemDocument>(
            Builders<ItemDocument>.IndexKeys.Ascending(value => value.JobId).Ascending(value => value.Position),
            new CreateIndexOptions { Name = "ux_job_item_position", Unique = true }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _items.Indexes.CreateOneAsync(new CreateIndexModel<ItemDocument>(
            Builders<ItemDocument>.IndexKeys.Ascending(value => value.JobId).Ascending(value => value.State).Ascending(value => value.Position),
            new CreateIndexOptions { Name = "ix_job_item_pending" }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateAsync(ValidationJobSnapshot job, IReadOnlyList<ValidationJobItem> items, CancellationToken cancellationToken = default)
    {
        await _jobs.InsertOneAsync(JobDocument.FromModel(job), cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await _items.InsertManyAsync(items.Select(ItemDocument.FromModel), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await _jobs.DeleteOneAsync(value => value.Id == job.JobId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
        (await _jobs.Find(value => value.Id == jobId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false))?.ToModel();

    public async Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(
        string sourceFileId,
        CancellationToken cancellationToken = default)
    {
        var successfulStates = new[]
        {
            ValidationJobState.Completed,
            ValidationJobState.CompletedWithErrors
        };
        var successful = await _jobs.Find(value =>
                value.SourceFileId == sourceFileId && successfulStates.Contains(value.State))
            .SortByDescending(value => value.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var latest = successful ?? await _jobs.Find(value => value.SourceFileId == sourceFileId)
            .SortByDescending(value => value.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return latest?.ToModel();
    }

    public async Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(string jobId, int skip, int take, CancellationToken cancellationToken = default) =>
        (await _items.Find(value => value.JobId == jobId).SortBy(value => value.Position).Skip(skip).Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Select(value => value.ToModel()).ToArray();

    public async Task<IReadOnlyList<ValidationJobItem>> GetPendingAsync(string jobId, int take, CancellationToken cancellationToken = default) =>
        (await _items.Find(value => value.JobId == jobId && value.State == ValidationJobItemState.Pending)
            .SortBy(value => value.Position).Limit(take).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(value => value.ToModel()).ToArray();

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
        var updated = await _jobs.UpdateOneAsync(
            value => value.Id == jobId && activeStates.Contains(value.State),
            Builders<JobDocument>.Update.Set(value => value.State, ValidationJobState.Failed)
                .Set(value => value.FailureReason, failureReason)
                .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow()),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated.ModifiedCount > 0;
    }

    public async Task SaveResultAsync(string jobId, int position, EmailValidationResult? result, string? failureReason, CancellationToken cancellationToken = default)
    {
        var state = result is null ? ValidationJobItemState.Failed : ValidationJobItemState.Completed;
        var update = Builders<ItemDocument>.Update.Set(value => value.State, state)
            .Set(value => value.ResultJson, result is null ? null : JsonSerializer.Serialize(result, JsonOptions))
            .Set(value => value.Error, failureReason);
        var updated = await _items.UpdateOneAsync(
            value => value.JobId == jobId && value.Position == position && value.State == ValidationJobItemState.Pending,
            update, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (updated.ModifiedCount == 0) return;

        var counters = Builders<JobDocument>.Update.Inc(value => value.ProcessedItems, 1)
            .Inc(value => value.FinalItems, result?.ResultState == ValidationResultState.Final ? 1 : 0)
            .Inc(value => value.ProvisionalItems, result?.ResultState == ValidationResultState.Provisional ? 1 : 0)
            .Inc(value => value.FailedItems, result is null ? 1 : 0)
            .Set(value => value.UpdatedAtUtc, _timeProvider.GetUtcNow());
        await _jobs.UpdateOneAsync(value => value.Id == jobId, counters, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
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
        public static JobDocument FromModel(ValidationJobSnapshot value) => new()
        {
            Id = value.JobId, CreatedAtUtc = value.CreatedAtUtc, State = value.State,
            TotalItems = value.TotalItems, ProcessedItems = value.ProcessedItems,
            FinalItems = value.FinalItems, ProvisionalItems = value.ProvisionalItems,
            FailedItems = value.FailedItems, UpdatedAtUtc = value.UpdatedAtUtc,
            FailureReason = value.FailureReason, EnableSmtp = value.EnableSmtp,
            SourceFileId = value.SourceFileId, SourceFileName = value.SourceFileName,
            EmailColumn = value.EmailColumn
        };
        public ValidationJobSnapshot ToModel() => new(Id, CreatedAtUtc, State, TotalItems, ProcessedItems,
            FinalItems, ProvisionalItems, FailedItems, UpdatedAtUtc, FailureReason, EnableSmtp,
            SourceFileId, SourceFileName, EmailColumn);
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
        public string? Error { get; set; }
        public static ItemDocument FromModel(ValidationJobItem value) => new()
        {
            Id = $"{value.JobId}:{value.Position}", JobId = value.JobId, Position = value.Position,
            Email = value.Email, State = value.State, Error = value.Error,
            ResultJson = value.Result is null ? null : JsonSerializer.Serialize(value.Result, JsonOptions)
        };
        public ValidationJobItem ToModel() => new(JobId, Position, Email, State,
            ResultJson is null ? null : JsonSerializer.Deserialize<EmailValidationResult>(ResultJson, JsonOptions), Error);
    }
}

public sealed class InMemoryValidationJobStore(TimeProvider timeProvider) : IValidationJobStore
{
    private readonly ConcurrentDictionary<string, ValidationJobSnapshot> _jobs = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, ValidationJobItem>> _items = new();
    private readonly object _sync = new();

    public Task CreateAsync(ValidationJobSnapshot job, IReadOnlyList<ValidationJobItem> items, CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryAdd(job.JobId, job)) throw new InvalidOperationException("Duplicate job id.");
        _items[job.JobId] = new(items.ToDictionary(value => value.Position));
        return Task.CompletedTask;
    }
    public Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.GetValueOrDefault(jobId));
    public Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(
        string sourceFileId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.Values
            .Where(job => string.Equals(job.SourceFileId, sourceFileId, StringComparison.Ordinal))
            .OrderByDescending(job => job.State is ValidationJobState.Completed or ValidationJobState.CompletedWithErrors)
            .ThenByDescending(job => job.CreatedAtUtc)
            .FirstOrDefault());
    public Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(string jobId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ValidationJobItem>>(_items.GetValueOrDefault(jobId)?.Values.OrderBy(value => value.Position).Skip(skip).Take(take).ToArray() ?? []);
    public Task<IReadOnlyList<ValidationJobItem>> GetPendingAsync(string jobId, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ValidationJobItem>>(_items.GetValueOrDefault(jobId)?.Values.Where(value => value.State == ValidationJobItemState.Pending).OrderBy(value => value.Position).Take(take).ToArray() ?? []);
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
    public Task SaveResultAsync(string jobId, int position, EmailValidationResult? result, string? failureReason, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_items.TryGetValue(jobId, out var items) || !items.TryGetValue(position, out var item) || item.State != ValidationJobItemState.Pending)
                return Task.CompletedTask;
            items[position] = item with { State = result is null ? ValidationJobItemState.Failed : ValidationJobItemState.Completed, Result = result, Error = failureReason };
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
        return Task.CompletedTask;
    }
}

public sealed class AzureServiceBusValidationJobDispatcher(IOptions<EmailValidationOptions> options) : IValidationJobDispatcher, IAsyncDisposable
{
    private readonly ValidationJobsOptions _options = options.Value.Jobs;
    private ServiceBusClient? _client;
    private ServiceBusSender? _sender;
    public async Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default)
    {
        _client ??= new ServiceBusClient(_options.ServiceBusConnectionString);
        _sender ??= _client.CreateSender(_options.QueueName);
        await _sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromString(jobId))
        {
            MessageId = Guid.NewGuid().ToString("N"),
            CorrelationId = jobId,
            Subject = "email-validation-job",
            ContentType = "text/plain"
        }, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync().ConfigureAwait(false);
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class DisabledValidationJobDispatcher : IValidationJobDispatcher
{
    public Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default) =>
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
