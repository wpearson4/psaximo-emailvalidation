using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class RevalidationExecutionTests
{
    [Fact]
    public async Task TwoProcessors_WithSameDelivery_ExecuteOneObservationAndFinalizeOnce()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var initial = Initial(clock.GetUtcNow());
        var store = new InMemoryValidationLifecycleStore(clock);
        await store.TrySaveAsync(initial, 0);
        var service = new BlockingService(clock);
        using var metrics = new RevalidationMetrics();
        var first = Processor(store, service, clock, metrics).ProcessAsync(Message(initial));
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = await Processor(store, service, clock, metrics).ProcessAsync(Message(initial));
        Assert.Equal(RevalidationProcessingDisposition.Stale, duplicate.Disposition);
        Assert.Equal(1, service.Calls);
        service.Release.TrySetResult();
        Assert.Equal(RevalidationProcessingDisposition.Completed, (await first).Disposition);
        var final = await store.GetAsync(initial.ValidationId);
        Assert.Equal(ValidationResultState.Final, final!.ResultState);
        Assert.Null(final.ExecutionLease);
        Assert.Equal(1, final.ExecutionFence);
        Assert.Equal(2, final.Attempts.Count);
    }

    [Fact]
    public async Task ExpiredOwner_CannotSaveOrRenewEvenBeforeTakeover()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var store = new InMemoryValidationLifecycleStore(clock);
        var initial = Initial(clock.GetUtcNow());
        await store.TrySaveAsync(initial, 0);
        var acquired = await store.TryAcquireExecutionAsync(initial.ValidationId, 1, 2, "owner-a", TimeSpan.FromSeconds(3));
        Assert.NotNull(acquired);
        Assert.False((await store.TrySaveAsync(acquired with { Version = 3 }, 2)).Applied);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(await store.RenewExecutionAsync(initial.ValidationId, acquired.ExecutionLease!, TimeSpan.FromSeconds(3)));
        Assert.False((await store.TrySaveExecutionAsync(acquired with { Version = 3, ExecutionLease = null },
            2, acquired.ExecutionLease!)).Applied);
    }

    [Fact]
    public async Task Processor_DropsResultAfterLeaseExpiry()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var store = new InMemoryValidationLifecycleStore(clock);
        var initial = Initial(clock.GetUtcNow());
        await store.TrySaveAsync(initial, 0);
        var service = new BlockingService(clock);
        using var metrics = new RevalidationMetrics();
        var work = Processor(store, service, clock, metrics).ProcessAsync(Message(initial));
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(3));
        service.Release.TrySetResult();
        Assert.Equal(RevalidationProcessingDisposition.Stale, (await work).Disposition);
        Assert.Equal(ValidationResultState.Provisional, (await store.GetAsync(initial.ValidationId))!.ResultState);
    }

    [Fact]
    public async Task Heartbeat_RenewsDuringLongWorkAndKeepsDuplicateOut()
    {
        var clock = TimeProvider.System;
        var store = new InMemoryValidationLifecycleStore(clock);
        var initial = Initial(clock.GetUtcNow());
        await store.TrySaveAsync(initial, 0);
        var service = new BlockingService(clock);
        using var metrics = new RevalidationMetrics();
        var options = Options.Create(new EmailValidationOptions());
        options.Value.Revalidation.ExecutionLeaseSeconds = 3;
        options.Value.Revalidation.ExecutionRenewalSeconds = 1;
        var worker = Processor(store, service, clock, metrics, options: options);
        var work = worker.ProcessAsync(Message(initial));
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstExpiry = (await store.GetAsync(initial.ValidationId))!.ExecutionLease!.ExpiresAt;
        await Task.Delay(TimeSpan.FromSeconds(4));
        var renewed = (await store.GetAsync(initial.ValidationId))!.ExecutionLease!;
        Assert.True(renewed.ExpiresAt > firstExpiry);
        Assert.True(renewed.ExpiresAt > clock.GetUtcNow());
        Assert.Equal(RevalidationProcessingDisposition.Stale, (await worker.ProcessAsync(Message(initial))).Disposition);
        service.Release.TrySetResult();
        Assert.Equal(RevalidationProcessingDisposition.Completed, (await work).Disposition);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task RenewalFailure_CancelsWorkAndCannotCommit()
    {
        var clock = TimeProvider.System;
        var inner = new InMemoryValidationLifecycleStore(clock);
        var initial = Initial(clock.GetUtcNow());
        await inner.TrySaveAsync(initial, 0);
        var store = new FailedRenewalStore(inner);
        var service = new BlockingService(clock);
        using var metrics = new RevalidationMetrics();
        var options = Options.Create(new EmailValidationOptions());
        options.Value.Revalidation.ExecutionLeaseSeconds = 3;
        options.Value.Revalidation.ExecutionRenewalSeconds = 1;
        var result = await Processor(store, service, clock, metrics, options: options)
            .ProcessAsync(Message(initial)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RevalidationProcessingDisposition.Stale, result.Disposition);
        // The processor's cancellation wait can finish before the service's catch
        // continuation runs. Wait for the service to observe cancellation explicitly.
        await service.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ValidationResultState.Provisional, (await inner.GetAsync(initial.ValidationId))!.ResultState);
    }

    [Fact]
    public async Task IntentionalDeferral_ChangesBrokerIdentityButPreservesObservationBudget()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var store = new InMemoryValidationLifecycleStore(clock);
        var initial = Initial(clock.GetUtcNow());
        await store.TrySaveAsync(initial, 0);
        var service = new BlockingService(clock);
        using var metrics = new RevalidationMetrics();
        var worker = Processor(store, service, clock, metrics, new CoolingThrottle(clock.GetUtcNow().AddMinutes(5)));
        var original = Message(initial);
        Assert.Equal(RevalidationProcessingDisposition.Rescheduled, (await worker.ProcessAsync(original)).Disposition);
        var deferred = (await store.GetAsync(initial.ValidationId))!;
        var dispatch = deferred.PendingRevalidation!.Message;
        Assert.NotEqual(original.MessageId, dispatch.MessageId);
        Assert.Equal(2, dispatch.DispatchGeneration);
        Assert.Equal(original.AttemptNumber, dispatch.AttemptNumber);
        Assert.Equal(1, deferred.AttemptNumber);
        Assert.Null(deferred.ExecutionLease);
        Assert.Equal(0, service.Calls);
        Assert.Equal(RevalidationProcessingDisposition.Stale, (await worker.ProcessAsync(original)).Disposition);
        var serializer = new JsonRevalidationMessageSerializer();
        Assert.True(serializer.TryDeserialize(serializer.Serialize(dispatch), out var republished, out _));
        Assert.Equal(dispatch.MessageId, republished!.MessageId);
        Assert.Equal(RevalidationProcessingDisposition.Rescheduled, (await worker.ProcessAsync(dispatch)).Disposition);
        var nextDispatch = (await store.GetAsync(initial.ValidationId))!.PendingRevalidation!.Message;
        Assert.Equal(dispatch.ScheduledRetryAt, nextDispatch.ScheduledRetryAt);
        Assert.NotEqual(dispatch.MessageId, nextDispatch.MessageId);
        Assert.Equal(3, nextDispatch.DispatchGeneration);
    }

    [Fact]
    public void VersionedDispatch_HasBoundedIdentityAndSafePoisonDiagnostics()
    {
        var message = Message(Initial(DateTimeOffset.UtcNow)) with { ValidationId = new string('x', 128) };
        Assert.True(message.MessageId.Length <= 128);
        Assert.NotEqual(message.MessageId, (message with { DispatchGeneration = 2 }).MessageId);
        var serializer = new JsonRevalidationMessageSerializer();
        Assert.False(serializer.TryDeserialize("{\"attemptNumber\":\"secret-recipient@example.test\"}"u8.ToArray(), out _, out var error));
        Assert.DoesNotContain("secret-recipient", error!, StringComparison.Ordinal);
        Assert.False(serializer.TryDeserialize(serializer.Serialize(message with { MessageVersion = 3 }), out _, out _));
    }

    [Fact]
    public void Recovery_RequeuesSameObservationWithNewGenerationAndEventuallyStopsCrashes()
    {
        var now = DateTimeOffset.UtcNow;
        var initial = Initial(now);
        var running = RevalidationExecution.Acquire(initial, 2, "old", TimeSpan.FromSeconds(2), now)!;
        Assert.Null(MongoValidationLifecycleStore.TryCreateRecovery(running, now, TimeSpan.FromMinutes(15)));
        var recovered = MongoValidationLifecycleStore.TryCreateRecovery(running, now.AddSeconds(3), TimeSpan.FromMinutes(15))!;
        Assert.Null(recovered.ExecutionLease);
        Assert.Equal(1, recovered.AttemptNumber);
        Assert.Equal(2, recovered.PendingRevalidation!.Message.AttemptNumber);
        Assert.Equal(2, recovered.DispatchGeneration);
        Assert.Equal(1, recovered.ExecutionRecoveryCount);
        Assert.Equal(running.ExecutionFence, recovered.ExecutionFence);
        var exhausted = MongoValidationLifecycleStore.TryCreateRecovery(running with { ExecutionRecoveryCount = 3 },
            now.AddSeconds(3), TimeSpan.FromMinutes(15))!;
        Assert.Equal(ValidationLifecycleState.Failed, exhausted.LifecycleState);
        Assert.Equal(ValidationResultState.Final, exhausted.ResultState);
        Assert.Null(exhausted.PendingRevalidation);
    }

    private static EmailRevalidationProcessor Processor(IValidationLifecycleStore store, IEmailValidationService service,
        TimeProvider clock, IRevalidationMetrics metrics, ISmtpProbeThrottle? throttle = null, IOptions<EmailValidationOptions>? options = null)
    {
        options ??= Options.Create(new EmailValidationOptions());
        var dispatcher = new UnavailableDispatcher();
        var schedule = new Schedule();
        var coordinator = new ValidationLifecycleCoordinator(store, new FinalPolicy(), schedule, dispatcher, metrics,
            clock, options, NullLogger<ValidationLifecycleCoordinator>.Instance);
        return new(store, service, coordinator, dispatcher, throttle ?? new OpenThrottle(), schedule, metrics, clock, options: options);
    }

    private static ValidationLifecycle Initial(DateTimeOffset now)
    {
        var result = Result(now.AddMinutes(-1));
        return new()
        {
            ValidationId = Guid.NewGuid().ToString("N"), NormalizedEmail = result.NormalizedEmail!, MailboxKey = result.MailboxKey,
            Request = new(true), ResultState = ValidationResultState.Provisional, AttemptNumber = 1, MaximumAttempts = 2,
            CurrentResult = result, FirstValidatedAt = now.AddMinutes(-1), LastValidatedAt = now.AddMinutes(-1),
            LifecycleState = ValidationLifecycleState.RetryWaiting, RetryScheduled = true, NextRetryAt = now,
            DispatchGeneration = 1, Version = 1,
            Attempts = [new(1, result.Status, result.SubStatus, result.Confidence, result.MailProvider, [], now.AddMinutes(-1), ValidationResultSource.LiveValidation, now)]
        };
    }

    private static EmailRevalidationMessageV1 Message(ValidationLifecycle lifecycle) => new(lifecycle.ValidationId, 2, 2,
        lifecycle.FirstValidatedAt, lifecycle.LastValidatedAt, lifecycle.NextRetryAt!.Value, "GenericSmtp",
        EmailValidationStatus.Unknown, DetailedStatus.Unknown, "1", 2, lifecycle.DispatchGeneration);

    private static EmailValidationResult Result(DateTimeOffset now) => new()
    {
        Email = "person@example.test", NormalizedEmail = "person@example.test", MailboxKey = MailboxIdentity.Create("person@example.test").Key,
        Status = EmailValidationStatus.Unknown, Checks = new(), Metadata = new(new("1", "1", "1", "1"), now),
        MailboxEvidenceObservedAt = now, ProbeAttempted = true, ReasonCodes = [ReasonCode.TemporaryFailure]
    };

    private sealed class BlockingService(TimeProvider clock) : IEmailValidationService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EmailValidationResult> ValidateAsync(string email, EmailValidationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved.TrySetResult(); throw; }
            return Result(clock.GetUtcNow()) with { Status = EmailValidationStatus.Valid, ReasonCodes = [ReasonCode.MailboxAccepted] };
        }
    }
    private sealed class FinalPolicy : IRevalidationPolicy
    {
        public RevalidationDecision Evaluate(EmailValidationResult result, RevalidationContext context) => new(false, null, 2);
    }
    private sealed class Schedule : IRevalidationSchedulePolicy
    {
        public RevalidationSchedule CreateSchedule(RevalidationScheduleContext context) => new(context.CurrentCooldownUntil ?? context.Now.AddMinutes(5), "test");
    }
    private sealed class UnavailableDispatcher : IRevalidationOutboxDispatcher
    {
        public Task<RevalidationScheduleResult?> DispatchAsync(string validationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RevalidationScheduleResult?>(new(false, "unavailable", DateTimeOffset.UtcNow));
        public Task<int> DispatchPendingAsync(int maximumCount, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
    private sealed class OpenThrottle : ISmtpProbeThrottle
    {
        public ValueTask<ISmtpThrottleLease> AcquireAsync(SmtpThrottleContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class CoolingThrottle(DateTimeOffset at) : ISmtpProbeThrottle
    {
        public ProviderThrottleAvailability GetAvailability(SmtpThrottleContext context) => new(false, at, "cooldown");
        public ValueTask<ISmtpThrottleLease> AcquireAsync(SmtpThrottleContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
    private sealed class FailedRenewalStore(InMemoryValidationLifecycleStore inner) : IValidationLifecycleStore
    {
        public Task<ValidationLifecycle?> GetAsync(string id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);
        public Task<ValidationLifecycle?> GetActiveByEmailAsync(string email, CancellationToken cancellationToken = default) => inner.GetActiveByEmailAsync(email, cancellationToken);
        public Task<LifecycleWriteResult> TrySaveAsync(ValidationLifecycle lifecycle, long version, CancellationToken cancellationToken = default) => inner.TrySaveAsync(lifecycle, version, cancellationToken);
        public Task<ValidationLifecycle?> TryAcquireExecutionAsync(string id, long version, int attempt, string owner, TimeSpan lease, CancellationToken cancellationToken = default) =>
            inner.TryAcquireExecutionAsync(id, version, attempt, owner, lease, cancellationToken);
        public Task<bool> RenewExecutionAsync(string id, RevalidationExecutionLease lease, TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<LifecycleWriteResult> TrySaveExecutionAsync(ValidationLifecycle lifecycle, long version, RevalidationExecutionLease lease, CancellationToken cancellationToken = default) =>
            inner.TrySaveExecutionAsync(lifecycle, version, lease, cancellationToken);
    }
}
