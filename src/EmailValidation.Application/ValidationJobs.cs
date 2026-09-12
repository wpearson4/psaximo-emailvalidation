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
public enum ValidationJobDispatchState { Pending, Claimed, Published }

public sealed class ValidationJobNotFoundException(string jobId)
    : Exception($"Validation job '{jobId}' does not exist.");

public sealed class ValidationJobSourceFileCompletedException()
    : Exception("This source file already has a completed validation job.");

public sealed class ValidationJobSourceFileActiveException()
    : Exception("This source file already has a validation job in progress.");

public static class ValidationJobIdentity
{
    public static string FromSourceFileId(string sourceFileId, string? tenantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileId);
        var sourceIdentity = string.IsNullOrWhiteSpace(tenantId)
            ? sourceFileId.Trim()
            : $"{tenantId.Trim()}\n{sourceFileId.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceIdentity));
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
    IReadOnlyList<int>? SourcePositions = null,
    string? TenantId = null);

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
    string? EmailColumn = null,
    ValidationJobDispatchState DispatchState = ValidationJobDispatchState.Pending,
    string? DispatchId = null,
    int DispatchChunkCount = 0,
    string? TenantId = null);

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
    Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(
        string sourceFileId,
        string? tenantId = null,
        CancellationToken cancellationToken = default);
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
    Task QueueDispatchAsync(
        string jobId,
        string dispatchId,
        int chunkCount,
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

/// <summary>
/// Reconciles domain-scoped accept-all evidence without changing mailbox-scoped evidence.
/// A later independent observation can confirm that a public SMTP endpoint is
/// non-discriminating, so final candidate rows for the same domain should expose the
/// stronger domain conclusion even though those mailboxes were not probed again.
/// </summary>
public static class ValidationJobResultEvidenceReconciler
{
    public static EmailValidationResult Normalize(EmailValidationResult result)
    {
        if (!HasConfirmedAcceptAll(result) ||
            result.Status != EmailValidationStatus.Unknown ||
            !HasCandidateArtifacts(result))
            return result;

        return ApplyConfirmedAcceptAll(result, result.DomainIntelligence!);
    }

    public static EmailValidationResult? ReconcilePeer(
        EmailValidationResult peer,
        EmailValidationResult confirmed)
    {
        if (peer.ResultState != ValidationResultState.Final ||
            peer.Status != EmailValidationStatus.Unknown ||
            !HasCandidateArtifacts(peer) ||
            !HasConfirmedAcceptAll(confirmed) ||
            !string.Equals(Domain(peer), Domain(confirmed), StringComparison.OrdinalIgnoreCase))
            return null;

        return ApplyConfirmedAcceptAll(peer, confirmed.DomainIntelligence!);
    }

    public static string? Domain(EmailValidationResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.DomainIntelligence?.Domain))
            return result.DomainIntelligence.Domain.Trim().TrimEnd('.').ToLowerInvariant();
        var email = result.NormalizedEmail ?? result.Email;
        var separator = email.LastIndexOf('@');
        return separator >= 0 && separator < email.Length - 1
            ? email[(separator + 1)..].Trim().TrimEnd('.').ToLowerInvariant()
            : null;
    }

    public static string? Domain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var separator = email.LastIndexOf('@');
        return separator >= 0 && separator < email.Length - 1
            ? email[(separator + 1)..].Trim().TrimEnd('.').ToLowerInvariant()
            : null;
    }

    private static bool HasConfirmedAcceptAll(EmailValidationResult result) =>
        result.DomainIntelligence?.CatchAll.HasConfirmedAcceptAllEvidence == true;

    private static bool HasCandidateArtifacts(EmailValidationResult result) =>
        result.DomainIntelligence?.CatchAll.ReasonCode == CatchAllReasonCode.AcceptAllCandidate ||
        result.CatchAllEvidence?.ReasonCode == CatchAllReasonCode.AcceptAllCandidate ||
        result.ReasonCodes.Contains(ReasonCode.AcceptAllCandidate) ||
        result.UnknownContext?.Cause == UnknownCause.AcceptAllPendingConfirmation ||
        result.SubStatus == DetailedStatus.AcceptAllCandidate ||
        result.DetailedStatuses.Contains(DetailedStatus.AcceptAllCandidate);

    private static EmailValidationResult ApplyConfirmedAcceptAll(
        EmailValidationResult result,
        DomainIntelligence confirmedDomain)
    {
        var confirmed = confirmedDomain.CatchAll;
        var reasons = result.ReasonCodes
            .Where(reason => reason is not ReasonCode.AcceptAllCandidate and not ReasonCode.RetryRecommended)
            .Append(ReasonCode.AcceptAllObserved)
            .Distinct()
            .ToArray();
        var details = result.DetailedStatuses
            .Where(detail => detail is not DetailedStatus.AcceptAllCandidate and not DetailedStatus.MailboxAccepted)
            .Append(DetailedStatus.AcceptAllConfirmed)
            .Distinct()
            .ToArray();
        var subStatuses = result.SubStatuses
            .Where(detail => detail is not DetailedStatus.AcceptAllCandidate and not DetailedStatus.MailboxAccepted)
            .Append(DetailedStatus.AcceptAllConfirmed)
            .Distinct()
            .ToArray();
        var confidenceEvidence = result.ConfidenceEvidence
            .Where(item => !string.Equals(item.Evidence, "Accept-all candidate", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!confidenceEvidence.Any(item =>
                string.Equals(item.Evidence, "Accept-all SMTP behavior", StringComparison.OrdinalIgnoreCase)))
            confidenceEvidence.Add(new(
                "Accept-all SMTP behavior",
                0,
                "Independent observations established that the public SMTP endpoint accepts arbitrary recipients."));

        var staged = result with
        {
            Confidence = Math.Max(result.Confidence, confirmed.Confidence),
            ConfidenceReason = "Mailbox existence is uncertain because the public SMTP endpoint accepts arbitrary recipients; this establishes accept-all behavior, not catch-all routing.",
            Checks = result.Checks with { CatchAll = confirmed.Status },
            DomainIntelligence = confirmedDomain,
            CatchAllEvidence = confirmed,
            CatchAll = new CatchAllValidationDetails(confirmed.Status, confirmed.Confidence),
            ReasonCodes = reasons,
            DetailedStatus = DetailedStatus.AcceptAllConfirmed,
            DetailedStatuses = details,
            SubStatus = DetailedStatus.AcceptAllConfirmed,
            SubStatuses = subStatuses,
            ConfidenceEvidence = confidenceEvidence,
            RetryAfter = null,
            RetryScheduled = false,
            Diagnostics = result.Diagnostics is null
                ? null
                : result.Diagnostics with
                {
                    CatchAllProbes = confirmed.Probes,
                    CatchAllAccepted = confirmed.Accepted,
                    CatchAllRejected = confirmed.Rejected,
                    CatchAllAmbiguous = confirmed.Ambiguous,
                    CatchAllDetail = confirmed.Detail,
                    CatchAllObservedAt = confirmed.ObservedAt ?? confirmedDomain.ObservedAt,
                    RetryAfter = null
                }
        };
        return staged with { UnknownContext = UnknownValidationContextBuilder.Build(staged) };
    }
}

public interface IValidationJobDispatcher
{
    Task EnqueueAsync(
        string jobId,
        string dispatchId,
        int chunkCount,
        CancellationToken cancellationToken = default);
}

public sealed record ValidationJobDispatch(
    string JobId,
    string DispatchId,
    int ChunkCount,
    int AttemptCount);

public interface IValidationJobDispatchOutbox
{
    Task<IReadOnlyList<ValidationJobDispatch>> ClaimPendingAsync(
        int take,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(
        string jobId,
        string dispatchId,
        string leaseOwner,
        CancellationToken cancellationToken = default);
    Task<bool> ReleaseAsync(
        string jobId,
        string dispatchId,
        string leaseOwner,
        DateTimeOffset retryAtUtc,
        string failureReason,
        CancellationToken cancellationToken = default);
}

public interface IValidationJobOutboxDispatcher
{
    Task<int> DispatchPendingAsync(int take, CancellationToken cancellationToken = default);
}

public interface IValidationJobService
{
    Task<ValidationJobSnapshot> CreateAsync(CreateValidationJobRequest request, CancellationToken cancellationToken = default);
    Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(
        string sourceFileId,
        string? tenantId = null,
        CancellationToken cancellationToken = default);
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
        var tenantId = string.IsNullOrWhiteSpace(request.TenantId) ? null : request.TenantId.Trim();
        var existing = string.IsNullOrWhiteSpace(sourceFileId)
            ? null
            : await store.GetBySourceFileIdAsync(sourceFileId, tenantId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ResolveExistingSourceFileAsync(existing, cancellationToken).ConfigureAwait(false);

        var jobId = !string.IsNullOrWhiteSpace(sourceFileId)
            ? ValidationJobIdentity.FromSourceFileId(sourceFileId, tenantId)
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
            EmailColumn: request.EmailColumn,
            DispatchState: ValidationJobDispatchState.Pending,
            DispatchId: Guid.NewGuid().ToString("N"),
            DispatchChunkCount: ChunkCount(inputs.Length),
            TenantId: tenantId);
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
        return (await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false))!;
    }

    public Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
        store.GetAsync(jobId, cancellationToken);

    public Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(
        string sourceFileId,
        string? tenantId = null,
        CancellationToken cancellationToken = default) =>
        store.GetBySourceFileIdAsync(sourceFileId.Trim(), tenantId?.Trim(), cancellationToken);

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

        await store.QueueDispatchAsync(
            existing.JobId,
            Guid.NewGuid().ToString("N"),
            ChunkCount(Math.Max(1, existing.TotalItems - existing.ProcessedItems)),
            cancellationToken).ConfigureAwait(false);
        return (await store.GetAsync(existing.JobId, cancellationToken).ConfigureAwait(false))!;
    }

    private int ChunkCount(int itemCount) => Math.Max(1,
        (itemCount + Math.Max(1, _options.ChunkSize) - 1) / Math.Max(1, _options.ChunkSize));
}

public sealed class ValidationJobOutboxDispatcher(
    IValidationJobDispatchOutbox outbox,
    IValidationJobDispatcher dispatcher,
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider,
    ILogger<ValidationJobOutboxDispatcher> logger) : IValidationJobOutboxDispatcher
{
    private readonly ValidationJobsOptions _options = options.Value.Jobs;

    public async Task<int> DispatchPendingAsync(int take, CancellationToken cancellationToken = default)
    {
        var leaseOwner = Guid.NewGuid().ToString("N");
        var claimed = await outbox.ClaimPendingAsync(
            Math.Max(1, take), leaseOwner, TimeSpan.FromSeconds(_options.OutboxLeaseSeconds), cancellationToken)
            .ConfigureAwait(false);
        var completed = 0;
        foreach (var dispatch in claimed)
        {
            try
            {
                await dispatcher.EnqueueAsync(
                    dispatch.JobId, dispatch.DispatchId, dispatch.ChunkCount, cancellationToken).ConfigureAwait(false);
                if (await outbox.CompleteAsync(
                        dispatch.JobId, dispatch.DispatchId, leaseOwner, cancellationToken).ConfigureAwait(false))
                    completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(8, dispatch.AttemptCount))));
                logger.LogWarning(exception,
                    "Validation job {JobId} dispatch {DispatchId} failed; retrying after {RetryAtUtc}",
                    dispatch.JobId, dispatch.DispatchId, timeProvider.GetUtcNow().Add(delay));
                await outbox.ReleaseAsync(
                    dispatch.JobId,
                    dispatch.DispatchId,
                    leaseOwner,
                    timeProvider.GetUtcNow().Add(delay),
                    Truncate(exception.Message),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        return completed;
    }

    private static string Truncate(string value) => value.Length <= 1024 ? value : value[..1024];
}

public sealed class ValidationJobProcessor(
    IValidationJobStore store,
    IDomainValidationScheduler scheduler,
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
                var request = new EmailValidationRequest(
                    job.EnableSmtp, TenantId: job.TenantId, JobId: job.JobId);
                var work = items.Select(item => new ValidationWorkItem(
                    item.Position, item.Email, request)).ToArray();
                await foreach (var scheduled in scheduler.ScheduleStreamingAsync(work, cancellationToken))
                {
                    if (scheduled.FailureReason is null)
                    {
                        if (!await store.CompleteClaimAsync(
                                jobId, checked((int)scheduled.Sequence), leaseOwner, scheduled.Result, null,
                                cancellationToken).ConfigureAwait(false))
                            logger.LogWarning(
                                "Validation job {JobId} item {Position} completed after its lease was lost",
                                jobId, scheduled.Sequence);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Validation job {JobId} item {Position} failed: {FailureReason}",
                            jobId, scheduled.Sequence, scheduled.FailureReason);
                        await store.CompleteClaimAsync(
                            jobId, checked((int)scheduled.Sequence), leaseOwner, null,
                            scheduled.FailureReason, cancellationToken).ConfigureAwait(false);
                    }
                }
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
