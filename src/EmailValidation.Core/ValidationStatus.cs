using Microsoft.Extensions.Logging;

namespace EmailValidation.Core;

public enum ValidationLifecycleState
{
    Unspecified = 0,
    Requested = 1,
    Validating = 2,
    Provisional = 3,
    RetryWaiting = 4,
    Revalidating = 5,
    Final = 6,
    Failed = 7,
    RetryScheduled = 8
}

public enum ValidationProgressStage
{
    Unspecified = 0,
    Requested = 1,
    Started = 2,
    DomainChecks = 3,
    ProviderChecks = 4,
    SmtpValidation = 5,
    PersistedIntelligence = 6,
    Provisional = 7,
    RetryWaiting = 8,
    Revalidating = 9,
    Final = 10,
    Failed = 11,
    RetryScheduled = 12
}

public record ValidationStatusSnapshot
{
    public required string ValidationId { get; init; }
    public required ValidationLifecycleState LifecycleState { get; init; }
    public ValidationProgressStage CurrentStage { get; init; }
    public EmailValidationStatus? Status { get; init; }
    public DetailedStatus? SubStatus { get; init; }
    public required ValidationResultState ResultState { get; init; }
    public double? Confidence { get; init; }
    public string? ConfidenceReason { get; init; }
    public int AttemptNumber { get; init; }
    public int MaximumAttempts { get; init; }
    public bool IsRunning { get; init; }
    public bool ResultReused { get; init; }
    public bool RetryScheduled { get; init; }
    public string? RetryReason { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public DateTimeOffset? CooldownUntil { get; init; }
    public string? Provider { get; init; }
    public string? StatusMessage { get; init; }
    public DateTimeOffset? RequestedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FirstValidatedAt { get; init; }
    public DateTimeOffset? LastUpdatedAt { get; init; }
    public DateTimeOffset? FinalizedAt { get; init; }
    public long Sequence { get; init; }
}

public sealed record ValidationStatusChanged : ValidationStatusSnapshot
{
    public required DateTimeOffset OccurredAt { get; init; }
    public TimeSpan? EstimatedRetryIn { get; init; }
}

public static class ValidationStatusMapper
{
    public static ValidationStatusSnapshot ToSnapshot(ValidationLifecycle lifecycle)
    {
        var state = ResolveState(lifecycle);
        var hasResult = lifecycle.AttemptNumber > 0;
        var result = lifecycle.CurrentResult;
        return new()
        {
            ValidationId = lifecycle.ValidationId,
            LifecycleState = state,
            CurrentStage = ResolveStage(lifecycle.CurrentStage, state),
            Status = hasResult ? result.Status : null,
            SubStatus = hasResult ? result.SubStatus : null,
            ResultState = lifecycle.ResultState,
            Confidence = hasResult ? result.Confidence : null,
            ConfidenceReason = hasResult ? result.ConfidenceReason : null,
            AttemptNumber = lifecycle.AttemptNumber,
            MaximumAttempts = lifecycle.MaximumAttempts,
            IsRunning = state is ValidationLifecycleState.Validating or ValidationLifecycleState.Revalidating,
            ResultReused = lifecycle.ResultReused,
            RetryScheduled = lifecycle.RetryScheduled,
            RetryReason = lifecycle.RetryReason,
            RetryAt = lifecycle.NextRetryAt,
            CooldownUntil = result.RetryAfter,
            Provider = hasResult ? result.MailProvider.ToString() : null,
            StatusMessage = lifecycle.StatusMessage ?? Message(state, lifecycle.RetryScheduled),
            RequestedAt = lifecycle.RequestedAt,
            StartedAt = lifecycle.StartedAt,
            FirstValidatedAt = lifecycle.AttemptNumber > 0 ? lifecycle.FirstValidatedAt : null,
            LastUpdatedAt = lifecycle.LastUpdatedAt ??
                (lifecycle.AttemptNumber > 0 ? lifecycle.LastValidatedAt : lifecycle.StartedAt ?? lifecycle.RequestedAt),
            FinalizedAt = lifecycle.FinalizedAt,
            Sequence = lifecycle.Sequence > 0 ? lifecycle.Sequence : Math.Max(1, lifecycle.Version)
        };
    }

    public static ValidationStatusChanged ToEvent(ValidationLifecycle lifecycle, DateTimeOffset occurredAt)
    {
        return ToEvent(ToSnapshot(lifecycle), occurredAt);
    }

    public static ValidationStatusChanged ToEvent(ValidationStatusSnapshot snapshot, DateTimeOffset occurredAt)
    {
        return new()
        {
            ValidationId = snapshot.ValidationId,
            LifecycleState = snapshot.LifecycleState,
            CurrentStage = snapshot.CurrentStage,
            Status = snapshot.Status,
            SubStatus = snapshot.SubStatus,
            ResultState = snapshot.ResultState,
            Confidence = snapshot.Confidence,
            ConfidenceReason = snapshot.ConfidenceReason,
            AttemptNumber = snapshot.AttemptNumber,
            MaximumAttempts = snapshot.MaximumAttempts,
            IsRunning = snapshot.IsRunning,
            ResultReused = snapshot.ResultReused,
            RetryScheduled = snapshot.RetryScheduled,
            RetryReason = snapshot.RetryReason,
            RetryAt = snapshot.RetryAt,
            CooldownUntil = snapshot.CooldownUntil,
            Provider = snapshot.Provider,
            StatusMessage = snapshot.StatusMessage,
            RequestedAt = snapshot.RequestedAt,
            StartedAt = snapshot.StartedAt,
            FirstValidatedAt = snapshot.FirstValidatedAt,
            LastUpdatedAt = snapshot.LastUpdatedAt,
            FinalizedAt = snapshot.FinalizedAt,
            Sequence = snapshot.Sequence,
            OccurredAt = occurredAt,
            EstimatedRetryIn = snapshot.RetryAt is { } retryAt
                ? TimeSpan.FromTicks(Math.Max(0, (retryAt - occurredAt).Ticks))
                : null
        };
    }

    private static ValidationLifecycleState ResolveState(ValidationLifecycle lifecycle)
    {
        if (lifecycle.LifecycleState != ValidationLifecycleState.Unspecified)
            return lifecycle.LifecycleState;
        if (lifecycle.ResultState == ValidationResultState.Final)
            return ValidationLifecycleState.Final;
        return lifecycle.RetryScheduled
            ? ValidationLifecycleState.RetryWaiting
            : ValidationLifecycleState.Provisional;
    }

    private static ValidationProgressStage ResolveStage(
        ValidationProgressStage stage,
        ValidationLifecycleState state)
    {
        if (stage != ValidationProgressStage.Unspecified) return stage;
        return state switch
        {
            ValidationLifecycleState.Requested => ValidationProgressStage.Requested,
            ValidationLifecycleState.Validating => ValidationProgressStage.Started,
            ValidationLifecycleState.Provisional => ValidationProgressStage.Provisional,
            ValidationLifecycleState.RetryScheduled => ValidationProgressStage.RetryScheduled,
            ValidationLifecycleState.RetryWaiting => ValidationProgressStage.RetryWaiting,
            ValidationLifecycleState.Revalidating => ValidationProgressStage.Revalidating,
            ValidationLifecycleState.Final => ValidationProgressStage.Final,
            ValidationLifecycleState.Failed => ValidationProgressStage.Failed,
            _ => ValidationProgressStage.Unspecified
        };
    }

    private static string Message(ValidationLifecycleState state, bool retryScheduled) => state switch
    {
        ValidationLifecycleState.Requested => "Validation requested.",
        ValidationLifecycleState.Validating => "Validation started.",
        ValidationLifecycleState.Provisional when !retryScheduled => "Validation is provisional.",
        ValidationLifecycleState.RetryScheduled => "Automatic revalidation is being scheduled.",
        ValidationLifecycleState.RetryWaiting => "Validation is waiting for automatic revalidation.",
        ValidationLifecycleState.Revalidating => "Automatic revalidation started.",
        ValidationLifecycleState.Final => "Validation completed.",
        ValidationLifecycleState.Failed => "Validation failed.",
        _ => "Validation status updated."
    };
}

public sealed class ValidationStatusQueryService(IValidationLifecycleStore store) : IValidationStatusQueryService
{
    public async Task<ValidationStatusSnapshot?> GetAsync(
        string validationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(validationId)) return null;
        var lifecycle = await store.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
        return lifecycle is null ? null : ValidationStatusMapper.ToSnapshot(lifecycle);
    }
}

public sealed class UnrestrictedValidationAccessPolicy : IValidationAccessPolicy
{
    public Task<bool> CanAccessAsync(
        string validationId,
        ValidationAccessContext context,
        CancellationToken cancellationToken = default) => Task.FromResult(true);
}

public sealed class ValidationLifecycleProgressReporter(
    IValidationLifecycleStore store,
    IValidationStatusPublisher publisher,
    TimeProvider timeProvider,
    Microsoft.Extensions.Logging.ILogger<ValidationLifecycleProgressReporter> logger) : IValidationProgressReporter
{
    public async Task ReportAsync(
        string validationId,
        ValidationProgressStage stage,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(validationId)) return;
        try
        {
            for (var collision = 0; collision < 3; collision++)
            {
                var current = await store.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
                if (current is null || current.LifecycleState is ValidationLifecycleState.Final or ValidationLifecycleState.Failed)
                    return;
                var now = timeProvider.GetUtcNow();
                var updated = current with
                {
                    CurrentStage = stage,
                    StatusMessage = message,
                    LastUpdatedAt = now,
                    Sequence = current.Sequence + 1,
                    Version = current.Version + 1
                };
                var saved = await store.TrySaveAsync(updated, current.Version, cancellationToken).ConfigureAwait(false);
                if (!saved.Applied) continue;
                try
                {
                    await publisher.PublishAsync(
                        ValidationStatusMapper.ToEvent(saved.Lifecycle!, now), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Validation progress {Stage} was persisted for {ValidationId} but could not be published",
                        stage, validationId);
                }
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Validation progress {Stage} could not be persisted for {ValidationId}", stage, validationId);
        }
    }
}
