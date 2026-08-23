using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EmailValidation.Application;

public enum ValidationJobState { Requested, Queued, Processing, Completed, CompletedWithErrors, Failed }
public enum ValidationJobItemState { Pending, Processing, Completed, Failed }

public sealed class ValidationJobNotFoundException(string jobId)
    : Exception($"Validation job '{jobId}' does not exist.");

public sealed record CreateValidationJobRequest(
    IReadOnlyList<string> Emails,
    bool EnableSmtp = true,
    string? JobId = null);

public sealed record ValidationJobSnapshot(
    string JobId,
    DateTimeOffset CreatedAtUtc,
    ValidationJobState State,
    int TotalItems,
    int ProcessedItems,
    int FinalItems,
    int ProvisionalItems,
    int FailedItems,
    DateTimeOffset UpdatedAtUtc,
    string? FailureReason = null,
    bool EnableSmtp = true);

public sealed record ValidationJobItem(
    string JobId,
    int Position,
    string Email,
    ValidationJobItemState State,
    EmailValidationResult? Result = null,
    string? Error = null);

public interface IValidationJobStore
{
    Task CreateAsync(ValidationJobSnapshot job, IReadOnlyList<ValidationJobItem> items, CancellationToken cancellationToken = default);
    Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(string jobId, int skip, int take, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ValidationJobItem>> GetPendingAsync(string jobId, int take, CancellationToken cancellationToken = default);
    Task SetStateAsync(string jobId, ValidationJobState state, string? failureReason = null, CancellationToken cancellationToken = default);
    Task SaveResultAsync(string jobId, int position, EmailValidationResult? result, string? failureReason, CancellationToken cancellationToken = default);
}

public interface IValidationJobDispatcher
{
    Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default);
}

public interface IValidationJobService
{
    Task<ValidationJobSnapshot> CreateAsync(CreateValidationJobRequest request, CancellationToken cancellationToken = default);
    Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(string jobId, int skip, int take, CancellationToken cancellationToken = default);
}

public interface IValidationJobProcessor
{
    Task ProcessAsync(string jobId, CancellationToken cancellationToken = default);
}

public interface IValidationJobInfrastructureInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IValidationJobMetrics
{
    void RecordCreated(int items);
    void RecordCompleted(ValidationJobState state, TimeSpan duration);
}

public sealed class ValidationJobMetrics : IValidationJobMetrics, IDisposable
{
    private readonly Meter _meter = new("EmailValidation.Jobs");
    private readonly Counter<long> _created;
    private readonly Counter<long> _completed;
    private readonly Histogram<double> _duration;
    public ValidationJobMetrics()
    {
        _created = _meter.CreateCounter<long>("email_validation.job.created");
        _completed = _meter.CreateCounter<long>("email_validation.job.completed");
        _duration = _meter.CreateHistogram<double>("email_validation.job.processing_duration", "ms");
    }
    public void RecordCreated(int items) => _created.Add(1, new KeyValuePair<string, object?>("items", items));
    public void RecordCompleted(ValidationJobState state, TimeSpan duration)
    {
        _completed.Add(1, new KeyValuePair<string, object?>("state", state.ToString()));
        _duration.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("state", state.ToString()));
    }
    public void Dispose() => _meter.Dispose();
}

public sealed class ValidationJobService(
    IValidationJobStore store,
    IValidationJobDispatcher queue,
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider,
    IValidationJobMetrics? metrics = null) : IValidationJobService
{
    private readonly ValidationJobsOptions _options = options.Value.Jobs;

    public async Task<ValidationJobSnapshot> CreateAsync(
        CreateValidationJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Emails is null || request.Emails.Count == 0)
            throw new ArgumentException("At least one email address is required.", nameof(request));
        if (request.Emails.Count > _options.MaximumItemsPerJob)
            throw new ArgumentException($"A job may contain at most {_options.MaximumItemsPerJob} items.", nameof(request));
        if (request.Emails.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Job email addresses cannot be empty.", nameof(request));

        var now = timeProvider.GetUtcNow();
        var jobId = string.IsNullOrWhiteSpace(request.JobId)
            ? Guid.NewGuid().ToString("N")
            : request.JobId.Trim();
        if (jobId.Length > 128 || jobId.Any(character => character is not
                (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
            throw new ArgumentException("JobId is invalid.", nameof(request));
        var job = new ValidationJobSnapshot(jobId, now, ValidationJobState.Requested,
            request.Emails.Count, 0, 0, 0, 0, now, EnableSmtp: request.EnableSmtp);
        var items = request.Emails.Select((email, position) =>
            new ValidationJobItem(jobId, position, email, ValidationJobItemState.Pending)).ToArray();
        await store.CreateAsync(job, items, cancellationToken).ConfigureAwait(false);
        metrics?.RecordCreated(items.Length);
        try
        {
            await queue.EnqueueAsync(jobId, cancellationToken).ConfigureAwait(false);
            await store.SetStateAsync(jobId, ValidationJobState.Queued, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return (await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false))!;
        }
        catch
        {
            await store.SetStateAsync(jobId, ValidationJobState.Failed, "The durable job message could not be queued.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
        store.GetAsync(jobId, cancellationToken);

    public Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(
        string jobId, int skip, int take, CancellationToken cancellationToken = default) =>
        store.GetResultsAsync(jobId, Math.Max(0, skip), Math.Clamp(take, 1, _options.MaximumResultPageSize), cancellationToken);
}

public sealed class ValidationJobProcessor(
    IValidationJobStore store,
    IEmailValidator validator,
    IOptions<EmailValidationOptions> options,
    ILogger<ValidationJobProcessor> logger,
    IValidationJobMetrics? metrics = null) : IValidationJobProcessor
{
    private readonly ValidationJobsOptions _options = options.Value.Jobs;

    public async Task ProcessAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var job = await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null) throw new ValidationJobNotFoundException(jobId);
        if (job.State is ValidationJobState.Completed or ValidationJobState.CompletedWithErrors) return;
        await store.SetStateAsync(jobId, ValidationJobState.Processing, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            while (true)
            {
                var items = await store.GetPendingAsync(jobId, _options.ChunkSize, cancellationToken)
                    .ConfigureAwait(false);
                if (items.Count == 0) break;
                await Parallel.ForEachAsync(items, new ParallelOptions
                {
                    MaxDegreeOfParallelism = _options.MaximumConcurrency,
                    CancellationToken = cancellationToken
                }, async (item, token) =>
                {
                    try
                    {
                        var result = await validator.ValidateAsync(item.Email,
                            new EmailValidationRequest(job.EnableSmtp), token).ConfigureAwait(false);
                        await store.SaveResultAsync(jobId, item.Position, result, null, token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "Validation job {JobId} item {Position} failed", jobId, item.Position);
                        await store.SaveResultAsync(jobId, item.Position, null, exception.Message, token).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }

            var completed = await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            var finalState = completed!.FailedItems == 0 ? ValidationJobState.Completed : ValidationJobState.CompletedWithErrors;
            await store.SetStateAsync(jobId, finalState,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            metrics?.RecordCompleted(finalState, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await store.SetStateAsync(jobId, ValidationJobState.Failed, exception.Message, CancellationToken.None)
                .ConfigureAwait(false);
            metrics?.RecordCompleted(ValidationJobState.Failed, stopwatch.Elapsed);
            throw;
        }
    }
}
