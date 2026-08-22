namespace EmailValidation.Core;

public interface IEmailValidationService
{
    Task<EmailValidationResult> ValidateAsync(
        string email,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRevalidationPolicy
{
    RevalidationDecision Evaluate(EmailValidationResult result, RevalidationContext context);
}

public interface IRevalidationSchedulePolicy
{
    RevalidationSchedule CreateSchedule(RevalidationScheduleContext context);
}

public interface IRevalidationScheduler
{
    Task<RevalidationScheduleResult> ScheduleAsync(
        RevalidationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IValidationLifecycleStore
{
    Task<ValidationLifecycle?> GetAsync(
        string validationId,
        CancellationToken cancellationToken = default);
    Task<ValidationLifecycle?> GetActiveByEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken = default);
    Task<LifecycleWriteResult> TrySaveAsync(
        ValidationLifecycle lifecycle,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}

public interface IRevalidationOutbox
{
    Task<PendingRevalidation?> TryClaimAsync(
        string validationId,
        TimeSpan lease,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetPendingValidationIdsAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
    Task<bool> MarkScheduledAsync(
        string validationId,
        string messageId,
        RevalidationScheduleResult result,
        CancellationToken cancellationToken = default);
    Task ReleaseAsync(
        string validationId,
        string messageId,
        string? errorCode,
        CancellationToken cancellationToken = default);
}

public interface IRevalidationOutboxDispatcher
{
    Task<RevalidationScheduleResult?> DispatchAsync(
        string validationId,
        CancellationToken cancellationToken = default);
    Task<int> DispatchPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
}

public interface IValidationLifecycleCoordinator
{
    Task<ValidationLifecycleStartResult> BeginAsync(
        string email,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ValidationLifecycleStartResult(
            request.ValidationId ?? Guid.NewGuid().ToString("N"), null, false));

    Task FailAsync(
        string validationId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task<ValidationLifecycleResult> ProcessInitialResultAsync(
        EmailValidationResult result,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default);
    Task<ValidationLifecycleResult> ProcessRetryResultAsync(
        string validationId,
        long expectedVersion,
        int expectedAttemptNumber,
        EmailValidationResult result,
        CancellationToken cancellationToken = default);
}

public interface IValidationStatusPublisher
{
    Task PublishAsync(ValidationStatusChanged status, CancellationToken cancellationToken = default);
}

public interface IValidationProgressReporter
{
    Task ReportAsync(
        string validationId,
        ValidationProgressStage stage,
        string message,
        CancellationToken cancellationToken = default);
}

public interface IValidationStatusSubscription
{
    IAsyncEnumerable<ValidationStatusChanged> SubscribeAsync(
        string validationId,
        long afterSequence = 0,
        CancellationToken cancellationToken = default);
}

public interface IValidationStatusQueryService
{
    Task<ValidationStatusSnapshot?> GetAsync(
        string validationId,
        CancellationToken cancellationToken = default);
}

public sealed record ValidationAccessContext(string? Subject, string? TenantId);

public interface IValidationAccessPolicy
{
    Task<bool> CanAccessAsync(
        string validationId,
        ValidationAccessContext context,
        CancellationToken cancellationToken = default);
}

public interface IEmailRevalidationProcessor
{
    Task<RevalidationProcessingResult> ProcessAsync(
        EmailRevalidationMessageV1 message,
        CancellationToken cancellationToken = default);
}

public interface IRevalidationMessageSerializer
{
    byte[] Serialize(EmailRevalidationMessageV1 message);
    bool TryDeserialize(ReadOnlyMemory<byte> payload, out EmailRevalidationMessageV1? message, out string? failureReason);
}

public interface IRevalidationInfrastructureInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IRevalidationMetrics
{
    void RecordQueueReceived(MailProvider provider);
    void RecordWorkerFailure();
    void RecordProcessingLatency(TimeSpan latency);
    void RecordScheduled(MailProvider provider);
    void RecordExecuted(MailProvider provider, bool reusedFreshResult);
    void RecordAlreadyFinal();
    void RecordRescheduled(MailProvider provider);
    void RecordFinalized(MailProvider provider, EmailValidationStatus previous, EmailValidationStatus current, TimeSpan timeToFinal, int attempts);
    void RecordExhausted(MailProvider provider);
    void RecordDuplicate();
    void RecordStale();
    void RecordDeadLettered();
    void RecordProvisional(MailProvider provider);
    RevalidationMetricsSnapshot GetSnapshot();
}
