using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace EmailValidation.Application;

public enum ValidationJobState { Requested, Queued, Processing, Completed, CompletedWithErrors, Failed }
public enum ValidationJobItemState { Pending, Processing, Completed, Failed }

public sealed class ValidationJobNotFoundException(string jobId)
    : Exception($"Validation job '{jobId}' does not exist.");

public sealed class ValidationJobSourceFileCompletedException()
    : Exception("This source file already has a completed validation job.");

public sealed class ValidationJobSourceFileActiveException()
    : Exception("This source file already has a validation job in progress.");

public static class ValidationJobIdentity
{
    public static string FromSourceFileId(string sourceFileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceFileId.Trim()));
        return $"file_{Convert.ToHexStringLower(hash)}";
    }
}

public sealed record CreateValidationJobRequest(
    IReadOnlyList<string> Emails,
    bool EnableSmtp = true,
    string? JobId = null,
    string? SourceFileId = null,
    string? SourceFileName = null,
    string? EmailColumn = null,
    IReadOnlyList<int>? SourcePositions = null);

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
    bool EnableSmtp = true,
    string? SourceFileId = null,
    string? SourceFileName = null,
    string? EmailColumn = null);

public sealed record ValidationJobItem(
    string JobId,
    int Position,
    string Email,
    ValidationJobItemState State,
    EmailValidationResult? Result = null,
    string? Error = null,
    string? LeaseOwner = null,
    DateTimeOffset? LeaseExpiresAtUtc = null);

public interface IValidationJobStore
{
    Task CreateAsync(ValidationJobSnapshot job, IReadOnlyList<ValidationJobItem> items, CancellationToken cancellationToken = default);
    Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(string sourceFileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(string jobId, int skip, int take, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ValidationJobItem>> ClaimPendingAsync(
        string jobId,
        int take,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
    Task<int> RenewClaimsAsync(
        string jobId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
    Task<int> ReleaseClaimsAsync(
        string jobId,
        string leaseOwner,
        CancellationToken cancellationToken = default);
    Task<bool> CompleteClaimAsync(
        string jobId,
        int position,
        string leaseOwner,
        EmailValidationResult? result,
        string? failureReason,
        CancellationToken cancellationToken = default);
    Task<ValidationJobState?> TryFinalizeAsync(
        string jobId,
        CancellationToken cancellationToken = default);
    Task SetStateAsync(string jobId, ValidationJobState state, string? failureReason = null, CancellationToken cancellationToken = default);
    Task<bool> TrySetFailedAsync(string jobId, string failureReason, CancellationToken cancellationToken = default);
}

public interface IValidationJobResultSink
{
    Task ProjectAsync(
        string jobId,
        string validationId,
        EmailValidationResult result,
        CancellationToken cancellationToken = default);
}

public interface IValidationJobResultProjector
{
    Task ProjectAsync(string validationId, CancellationToken cancellationToken = default);
}

public sealed class ValidationJobResultProjector(
    IValidationLifecycleStore lifecycles,
    IValidationJobResultSink sink) : IValidationJobResultProjector
{
    public async Task ProjectAsync(string validationId, CancellationToken cancellationToken = default)
    {
        var lifecycle = await lifecycles.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
        var jobId = lifecycle?.Request.JobId;
        if (lifecycle is null || string.IsNullOrWhiteSpace(jobId)) return;
        await sink.ProjectAsync(jobId, validationId, lifecycle.CurrentResult, cancellationToken)
            .ConfigureAwait(false);
    }
}

public interface IValidationJobDispatcher
{
    Task EnqueueAsync(string jobId, int chunkCount, CancellationToken cancellationToken = default);
}

public interface IValidationJobService
{
    Task<ValidationJobSnapshot> CreateAsync(CreateValidationJobRequest request, CancellationToken cancellationToken = default);
    Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(string sourceFileId, CancellationToken cancellationToken = default);
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
        if (request.SourcePositions is not null && request.SourcePositions.Count != request.Emails.Count)
            throw new ArgumentException("Source positions must correspond to the supplied email addresses.", nameof(request));
        var inputs = request.Emails
            .Select((email, index) => new
            {
                Email = email?.Trim(),
                Position = request.SourcePositions?[index] ?? index
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Email))
            .ToArray();
        if (inputs.Length == 0)
            throw new ArgumentException("At least one non-empty email address is required.", nameof(request));
        if (inputs.Length > _options.MaximumItemsPerJob)
            throw new ArgumentException($"A job may contain at most {_options.MaximumItemsPerJob} items.", nameof(request));
        if (inputs.Any(item => item.Position < 0) ||
            inputs.Select(item => item.Position).Distinct().Count() != inputs.Length)
            throw new ArgumentException("Source positions must be non-negative and unique.", nameof(request));

        var now = timeProvider.GetUtcNow();
        var sourceFileId = request.SourceFileId?.Trim();
        var existing = string.IsNullOrWhiteSpace(sourceFileId)
            ? null
            : await store.GetBySourceFileIdAsync(sourceFileId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ResolveExistingSourceFileAsync(existing, cancellationToken).ConfigureAwait(false);

        var jobId = !string.IsNullOrWhiteSpace(sourceFileId)
            ? ValidationJobIdentity.FromSourceFileId(sourceFileId)
            : string.IsNullOrWhiteSpace(request.JobId)
                ? Guid.NewGuid().ToString("N")
                : request.JobId.Trim();
        if (jobId.Length > 128 || jobId.Any(character => character is not
                (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
            throw new ArgumentException("JobId is invalid.", nameof(request));
        var job = new ValidationJobSnapshot(jobId, now, ValidationJobState.Requested,
            inputs.Length, 0, 0, 0, 0, now,
            EnableSmtp: request.EnableSmtp,
            SourceFileId: sourceFileId,
            SourceFileName: request.SourceFileName,
            EmailColumn: request.EmailColumn);
        var items = inputs.Select(input =>
            new ValidationJobItem(jobId, input.Position, input.Email!, ValidationJobItemState.Pending)).ToArray();
        try
        {
            await store.CreateAsync(job, items, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var concurrentlyCreated = await store.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (concurrentlyCreated is not null)
                return await ResolveExistingSourceFileAsync(concurrentlyCreated, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        metrics?.RecordCreated(items.Length);
        try
        {
            await queue.EnqueueAsync(jobId, ChunkCount(inputs.Length), cancellationToken).ConfigureAwait(false);
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

    public Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(
        string sourceFileId,
        CancellationToken cancellationToken = default) =>
        store.GetBySourceFileIdAsync(sourceFileId.Trim(), cancellationToken);

    public Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(
        string jobId, int skip, int take, CancellationToken cancellationToken = default) =>
        store.GetResultsAsync(jobId, Math.Max(0, skip), Math.Clamp(take, 1, _options.MaximumResultPageSize), cancellationToken);

    private async Task<ValidationJobSnapshot> ResolveExistingSourceFileAsync(
        ValidationJobSnapshot existing,
        CancellationToken cancellationToken)
    {
        if (existing.State is ValidationJobState.Completed or ValidationJobState.CompletedWithErrors)
            throw new ValidationJobSourceFileCompletedException();
        if (existing.State is not ValidationJobState.Failed)
            throw new ValidationJobSourceFileActiveException();

        await queue.EnqueueAsync(existing.JobId,
            ChunkCount(Math.Max(1, existing.TotalItems - existing.ProcessedItems)), cancellationToken).ConfigureAwait(false);
        await store.SetStateAsync(existing.JobId, ValidationJobState.Queued,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (await store.GetAsync(existing.JobId, cancellationToken).ConfigureAwait(false))!;
    }

    private int ChunkCount(int itemCount) => Math.Max(1,
        (itemCount + Math.Max(1, _options.ChunkSize) - 1) / Math.Max(1, _options.ChunkSize));
}

public sealed class ValidationJobProcessor(
    IValidationJobStore store,
    IEmailValidator validator,
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider,
    ILogger<ValidationJobProcessor> logger,
    IValidationJobMetrics? metrics = null) : IValidationJobProcessor
{
    private readonly ValidationJobsOptions _options = options.Value.Jobs;

    public async Task ProcessAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var job = await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null) throw new ValidationJobNotFoundException(jobId);
        if (job.State is ValidationJobState.Completed or ValidationJobState.CompletedWithErrors or ValidationJobState.Failed)
            return;
        await store.SetStateAsync(jobId, ValidationJobState.Processing, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var leaseOwner = Guid.NewGuid().ToString("N");
        var leaseDuration = TimeSpan.FromMinutes(Math.Max(1, _options.ItemLeaseMinutes));
        var leaseRenewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? leaseRenewal = null;
        try
        {
            var items = await store.ClaimPendingAsync(
                jobId, _options.ChunkSize, leaseOwner, leaseDuration, cancellationToken).ConfigureAwait(false);
            if (items.Count > 0)
            {
                leaseRenewal = RenewClaimsAsync(
                    jobId, leaseOwner, leaseDuration, leaseRenewalCancellation.Token);
                await Parallel.ForEachAsync(items, new ParallelOptions
                {
                    MaxDegreeOfParallelism = _options.MaximumConcurrency,
                    CancellationToken = cancellationToken
                }, async (item, token) =>
                {
                    try
                    {
                        var result = await validator.ValidateAsync(item.Email,
                            new EmailValidationRequest(job.EnableSmtp, JobId: job.JobId), token).ConfigureAwait(false);
                        if (!await store.CompleteClaimAsync(
                                jobId, item.Position, leaseOwner, result, null, token).ConfigureAwait(false))
                            logger.LogWarning(
                                "Validation job {JobId} item {Position} completed after its lease was lost",
                                jobId, item.Position);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "Validation job {JobId} item {Position} failed", jobId, item.Position);
                        await store.CompleteClaimAsync(
                            jobId, item.Position, leaseOwner, null, exception.Message, token).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }

            var finalState = await store.TryFinalizeAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (finalState is not null) metrics?.RecordCompleted(finalState.Value, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Validation job {JobId} chunk failed and will remain recoverable for broker redelivery", jobId);
            throw;
        }
        finally
        {
            await leaseRenewalCancellation.CancelAsync().ConfigureAwait(false);
            if (leaseRenewal is not null)
            {
                try { await leaseRenewal.ConfigureAwait(false); }
                catch (OperationCanceledException) when (leaseRenewalCancellation.IsCancellationRequested) { }
            }
            leaseRenewalCancellation.Dispose();
            await store.ReleaseClaimsAsync(jobId, leaseOwner, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RenewClaimsAsync(
        string jobId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, leaseDuration.Ticks / 3));
        using var timer = new PeriodicTimer(interval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await store.RenewClaimsAsync(jobId, leaseOwner, leaseDuration, cancellationToken).ConfigureAwait(false);
    }
}
