namespace EmailValidation.Core;

public sealed record RevalidationContext(
    int AttemptNumber,
    int? ExistingMaximumAttempts = null);

public sealed record RevalidationDecision(
    bool ShouldRetry,
    ReasonCode? Reason,
    int MaximumAttempts);

public sealed record RevalidationScheduleContext(
    EmailValidationResult Result,
    ReasonCode Reason,
    int AttemptNumber,
    DateTimeOffset Now,
    DateTimeOffset? CurrentCooldownUntil = null);

public sealed record RevalidationSchedule(
    DateTimeOffset ScheduledAt,
    string Reason);

public sealed record EmailRevalidationMessageV1(
    string ValidationId,
    int AttemptNumber,
    int MaximumAttempts,
    DateTimeOffset OriginalValidatedAt,
    DateTimeOffset PreviousAttemptAt,
    DateTimeOffset ScheduledRetryAt,
    string? Provider,
    EmailValidationStatus PreviousStatus,
    DetailedStatus PreviousSubStatus,
    string? ClassificationPolicyVersion,
    int MessageVersion = 1)
{
    public string MessageId => $"{ValidationId}:{AttemptNumber}";
}

public sealed record RevalidationRequest(
    EmailRevalidationMessageV1 Message,
    DateTimeOffset ScheduledAt);

public sealed record RevalidationScheduleResult(
    bool Succeeded,
    string MessageId,
    DateTimeOffset ScheduledAt,
    long? SequenceNumber = null,
    string? ErrorCode = null);

public sealed record ValidationAttemptRecord(
    int AttemptNumber,
    EmailValidationStatus Status,
    DetailedStatus SubStatus,
    double Confidence,
    MailProvider Provider,
    IReadOnlyList<ReasonCode> ReasonCodes,
    DateTimeOffset AttemptedAt,
    ValidationResultSource ResultSource,
    DateTimeOffset? RetryAfter);

public sealed record PendingRevalidation(
    EmailRevalidationMessageV1 Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset ScheduledAt,
    int DispatchAttempts = 0,
    DateTimeOffset? DispatchLeaseUntil = null,
    string? LastErrorCode = null);

public sealed record ValidationLifecycle
{
    public required string ValidationId { get; init; }
    public required string NormalizedEmail { get; init; }
    public required EmailValidationRequest Request { get; init; }
    public required ValidationResultState ResultState { get; init; }
    public required int AttemptNumber { get; init; }
    public required int MaximumAttempts { get; init; }
    public required EmailValidationResult CurrentResult { get; init; }
    public IReadOnlyList<ValidationAttemptRecord> Attempts { get; init; } = [];
    public DateTimeOffset FirstValidatedAt { get; init; }
    public DateTimeOffset LastValidatedAt { get; init; }
    public DateTimeOffset? FinalizedAt { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public bool RetryScheduled { get; init; }
    public PendingRevalidation? PendingRevalidation { get; init; }
    public ValidationLifecycleState LifecycleState { get; init; }
    public ValidationProgressStage CurrentStage { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset? RequestedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? LastUpdatedAt { get; init; }
    public bool ResultReused { get; init; }
    public string? RetryReason { get; init; }
    public string? StatusMessage { get; init; }
    public long Version { get; init; }
}

public sealed record ValidationLifecycleStartResult(
    string ValidationId,
    ValidationLifecycle? Lifecycle,
    bool Applied);

public sealed record LifecycleWriteResult(
    bool Applied,
    ValidationLifecycle? Lifecycle);

public enum RevalidationProcessingDisposition
{
    Completed,
    Rescheduled,
    Stale,
    AlreadyFinal,
    RetryInfrastructureFailure,
    DeadLetter
}

public sealed record RevalidationProcessingResult(
    RevalidationProcessingDisposition Disposition,
    string? DeadLetterReason = null,
    string? DeadLetterDescription = null);

public sealed record ValidationLifecycleResult(
    EmailValidationResult Result,
    ValidationLifecycle? Lifecycle,
    bool Applied,
    bool SchedulingSucceeded);

public sealed record RevalidationMetricsSnapshot(
    long Scheduled,
    long Executed,
    long SkippedDueToFreshResult,
    long SkippedAlreadyFinal,
    long Rescheduled,
    long Finalized,
    long Exhausted,
    long DuplicateMessages,
    long StaleMessages,
    long DeadLettered,
    long MicrosoftProvisional,
    long MicrosoftScheduled,
    long MicrosoftExecuted,
    long MicrosoftResolved,
    long MicrosoftFinalUnknown);
