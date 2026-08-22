using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class RevalidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Policy_RetriesOnlyTransientUnknownWithinBound()
    {
        var policy = new RevalidationPolicy(
            new StubProviderPolicies(new("Microsoft365", 1, 0, 60, 1)),
            Options(true));

        var retry = policy.Evaluate(Result(EmailValidationStatus.Unknown, ReasonCode.PolicyBlock), new(1));
        var exhausted = policy.Evaluate(Result(EmailValidationStatus.Unknown, ReasonCode.PolicyBlock), new(2, 2));
        var conclusive = policy.Evaluate(Result(EmailValidationStatus.Valid, ReasonCode.MailboxAccepted), new(1));
        var permanent = policy.Evaluate(Result(EmailValidationStatus.Invalid, ReasonCode.InvalidSyntax), new(1));

        Assert.True(retry.ShouldRetry);
        Assert.Equal(2, retry.MaximumAttempts);
        Assert.False(exhausted.ShouldRetry);
        Assert.False(conclusive.ShouldRetry);
        Assert.False(permanent.ShouldRetry);
    }

    [Fact]
    public void Schedule_UsesMaximumOfBackoffRetryAfterProviderAndLocalCooldown()
    {
        var policy = new RevalidationSchedulePolicy(
            new StubProviderPolicies(new("Microsoft365", 1, 0, 60, 1)),
            new StubBackoff(Now.AddMinutes(5)));
        var result = Result(EmailValidationStatus.Unknown, ReasonCode.PolicyBlock) with
        {
            RetryAfter = Now.AddMinutes(30)
        };

        var providerWins = policy.CreateSchedule(new(result, ReasonCode.PolicyBlock, 1, Now));
        var localWins = policy.CreateSchedule(new(
            result, ReasonCode.PolicyBlock, 1, Now, Now.AddMinutes(90)));

        Assert.Equal(Now.AddMinutes(60), providerWins.ScheduledAt);
        Assert.Equal(Now.AddMinutes(90), localWins.ScheduledAt);
    }

    [Fact]
    public void Schedule_UsesYahooPolicyWithoutProviderBranching()
    {
        var policy = new RevalidationSchedulePolicy(
            new StubProviderPolicies(new("Yahoo", 1, 0, 30, 1)),
            new StubBackoff(Now.AddMinutes(5)));
        var result = Result(EmailValidationStatus.Unknown, ReasonCode.ProviderVerificationBlocked) with
        {
            MailProvider = MailProvider.Yahoo
        };

        var schedule = policy.CreateSchedule(new(
            result, ReasonCode.ProviderVerificationBlocked, 1, Now));

        Assert.Equal(Now.AddMinutes(30), schedule.ScheduledAt);
    }

    [Fact]
    public async Task Lifecycle_PersistsBeforePublishingRequestedValidatingAndFinal()
    {
        var store = new MemoryLifecycleStore();
        var publisher = new RecordingPublisher(store);
        using var metrics = new RevalidationMetrics();
        var coordinator = Coordinator(store, new StubDispatcher(true), metrics, publisher);
        var request = new EmailValidationRequest(true, ValidationId: "validation-live-123");

        var started = await coordinator.BeginAsync("person@example.com", request);
        var completed = await coordinator.ProcessInitialResultAsync(
            Result(EmailValidationStatus.Valid, ReasonCode.MailboxAccepted),
            request with { ValidationId = started.ValidationId });

        Assert.True(completed.Applied);
        Assert.Equal("validation-live-123", completed.Result.ValidationId);
        Assert.Equal(
            [ValidationLifecycleState.Requested, ValidationLifecycleState.Validating, ValidationLifecycleState.Final],
            publisher.Events.Select(item => item.LifecycleState));
        Assert.Equal([1L, 2L, 3L], publisher.Events.Select(item => item.Sequence));
        Assert.True(publisher.EveryEventWasAlreadyPersisted);
    }

    [Fact]
    public async Task PublisherFailure_DoesNotRollBackCanonicalResult()
    {
        var store = new MemoryLifecycleStore();
        using var metrics = new RevalidationMetrics();
        var coordinator = Coordinator(store, new StubDispatcher(true), metrics, new ThrowingPublisher());

        var completed = await coordinator.ProcessInitialResultAsync(
            Result(EmailValidationStatus.Valid, ReasonCode.MailboxAccepted), new(true));

        Assert.True(completed.Applied);
        Assert.Equal(ValidationResultState.Final, store.Value!.ResultState);
        Assert.Equal(ValidationLifecycleState.Final, store.Value.LifecycleState);
    }

    [Fact]
    public async Task ValidationFailure_PersistsSafeFailedLifecycle()
    {
        var store = new MemoryLifecycleStore();
        var publisher = new RecordingPublisher(store);
        using var metrics = new RevalidationMetrics();
        var coordinator = Coordinator(store, new StubDispatcher(true), metrics, publisher);
        var validator = new LifecycleEmailValidator(new ThrowingValidationService(), coordinator);

        await Assert.ThrowsAsync<InvalidOperationException>(() => validator.ValidateAsync(
            "person@example.com", new(true, ValidationId: "validation-failed-123")));

        Assert.Equal(ValidationLifecycleState.Failed, store.Value!.LifecycleState);
        Assert.Equal(ValidationResultState.Final, store.Value.ResultState);
        Assert.Equal("Validation failed.", store.Value.StatusMessage);
        Assert.DoesNotContain("internal failure", store.Value.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Coordinator_PreservesValidationIdAndFinalizesSecondAttempt()
    {
        var store = new MemoryLifecycleStore();
        var dispatcher = new StubDispatcher(true);
        using var metrics = new RevalidationMetrics();
        var coordinator = Coordinator(store, dispatcher, metrics);

        var first = await coordinator.ProcessInitialResultAsync(
            Result(EmailValidationStatus.Unknown, ReasonCode.TemporaryFailure), new(true));

        Assert.Equal(ValidationResultState.Provisional, first.Result.ResultState);
        Assert.Equal(1, first.Result.AttemptNumber);
        Assert.Equal(2, first.Result.MaximumAttempts);
        Assert.NotNull(first.Result.ValidationId);
        Assert.Single(first.Lifecycle!.Attempts);
        Assert.NotNull(first.Lifecycle.PendingRevalidation);

        var second = await coordinator.ProcessRetryResultAsync(
            first.Result.ValidationId!, first.Lifecycle.Version, 2,
            Result(EmailValidationStatus.Valid, ReasonCode.MailboxAccepted));

        Assert.True(second.Applied);
        Assert.Equal(first.Result.ValidationId, second.Result.ValidationId);
        Assert.Equal(ValidationResultState.Final, second.Result.ResultState);
        Assert.Equal(2, second.Result.AttemptNumber);
        Assert.Equal(2, second.Lifecycle!.Attempts.Count);
        Assert.Null(second.Lifecycle.PendingRevalidation);
        Assert.NotNull(second.Result.FinalizedAt);
    }

    [Fact]
    public async Task Coordinator_ReusesIdenticalSingleFlightLifecycleInsteadOfCreatingAnotherAttempt()
    {
        var store = new MemoryLifecycleStore();
        using var metrics = new RevalidationMetrics();
        var coordinator = Coordinator(store, new StubDispatcher(true), metrics);
        var sharedResult = Result(EmailValidationStatus.Unknown, ReasonCode.TemporaryFailure);

        var first = await coordinator.ProcessInitialResultAsync(sharedResult, new(true));
        var duplicate = await coordinator.ProcessInitialResultAsync(sharedResult, new(true));

        Assert.Equal(first.Result.ValidationId, duplicate.Result.ValidationId);
        Assert.Equal(1, duplicate.Result.AttemptNumber);
        Assert.Single(duplicate.Lifecycle!.Attempts);
    }

    [Fact]
    public async Task Coordinator_LeavesDurableOutboxPendingWhenSchedulingFails()
    {
        var store = new MemoryLifecycleStore();
        using var metrics = new RevalidationMetrics();
        var coordinator = Coordinator(store, new StubDispatcher(false), metrics);

        var result = await coordinator.ProcessInitialResultAsync(
            Result(EmailValidationStatus.Unknown, ReasonCode.RateLimited), new(true));

        Assert.True(result.Applied);
        Assert.False(result.SchedulingSucceeded);
        Assert.NotNull(result.Lifecycle!.PendingRevalidation);
        Assert.False(result.Result.RetryScheduled);
    }

    [Fact]
    public async Task Coordinator_FinalizesUnknownWhenRetryBudgetIsExhausted()
    {
        var first = Lifecycle(ValidationResultState.Provisional, 1) with
        {
            CurrentResult = Result(EmailValidationStatus.Unknown, ReasonCode.Greylisted) with
            {
                ValidationId = "validation-123",
                ResultState = ValidationResultState.Provisional,
                AttemptNumber = 1,
                MaximumAttempts = 2
            },
            Attempts = [new(1, EmailValidationStatus.Unknown, DetailedStatus.Unknown, 0.25,
                MailProvider.Microsoft365, [ReasonCode.Greylisted], Now,
                ValidationResultSource.LiveValidation, Now.AddMinutes(5))]
        };
        var store = new MemoryLifecycleStore(first);
        using var metrics = new RevalidationMetrics();
        var coordinator = Coordinator(store, new StubDispatcher(true), metrics);

        var exhausted = await coordinator.ProcessRetryResultAsync(
            first.ValidationId, first.Version, 2,
            Result(EmailValidationStatus.Unknown, ReasonCode.Greylisted));

        Assert.Equal(ValidationResultState.Final, exhausted.Result.ResultState);
        Assert.Equal(EmailValidationStatus.Unknown, exhausted.Result.Status);
        Assert.False(exhausted.Result.RetryScheduled);
        Assert.NotNull(exhausted.Result.FinalizedAt);
        Assert.Equal(1, metrics.GetSnapshot().Exhausted);
    }

    [Fact]
    public async Task Processor_CompletesDuplicateAndAlreadyFinalMessagesWithoutValidation()
    {
        var final = Lifecycle(ValidationResultState.Final, 2);
        var store = new MemoryLifecycleStore(final);
        var service = new CountingValidationService(Result(EmailValidationStatus.Valid, ReasonCode.MailboxAccepted));
        using var metrics = new RevalidationMetrics();
        var processor = new EmailRevalidationProcessor(
            store, service, new StubCoordinator(), new StubDispatcher(true), new AvailableThrottle(),
            new RevalidationSchedulePolicy(new StubProviderPolicies(new("Generic", 1, 0, 15, 1)),
                new StubBackoff(Now)),
            metrics, new FixedTimeProvider(Now));
        var message = Message(final.ValidationId, 2);

        var alreadyFinal = await processor.ProcessAsync(message);

        Assert.Equal(RevalidationProcessingDisposition.AlreadyFinal, alreadyFinal.Disposition);
        Assert.Equal(0, service.Calls);

        var provisional = Lifecycle(ValidationResultState.Provisional, 2);
        store.Value = provisional;
        var duplicate = await processor.ProcessAsync(message);
        Assert.Equal(RevalidationProcessingDisposition.Stale, duplicate.Disposition);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public void VersionedMessage_RoundTripsWithDeterministicMessageId()
    {
        var serializer = new JsonRevalidationMessageSerializer();
        var original = Message("validation-123", 2);

        var success = serializer.TryDeserialize(serializer.Serialize(original), out var deserialized, out var failure);

        Assert.True(success, failure);
        Assert.Equal(original, deserialized);
        Assert.Equal("validation-123:2", deserialized!.MessageId);
    }

    [Fact]
    public void Serializer_RejectsMalformedPayload()
    {
        var serializer = new JsonRevalidationMessageSerializer();

        var success = serializer.TryDeserialize("not-json"u8.ToArray(), out var message, out var failure);

        Assert.False(success);
        Assert.Null(message);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task Processor_DeadLettersUnsupportedMessageVersionWithoutValidation()
    {
        var lifecycle = Lifecycle(ValidationResultState.Provisional, 1);
        var service = new CountingValidationService(Result(EmailValidationStatus.Valid, ReasonCode.MailboxAccepted));
        using var metrics = new RevalidationMetrics();
        var processor = new EmailRevalidationProcessor(
            new MemoryLifecycleStore(lifecycle), service, new StubCoordinator(), new StubDispatcher(true),
            new AvailableThrottle(),
            new RevalidationSchedulePolicy(new StubProviderPolicies(new("Generic", 1, 0, 15, 1)),
                new StubBackoff(Now)),
            metrics, new FixedTimeProvider(Now));

        var disposition = await processor.ProcessAsync(Message(lifecycle.ValidationId, 2) with { MessageVersion = 99 });

        Assert.Equal(RevalidationProcessingDisposition.DeadLetter, disposition.Disposition);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Processor_ReschedulesWhenProviderIsStillCoolingWithoutValidation()
    {
        var lifecycle = Lifecycle(ValidationResultState.Provisional, 1);
        var store = new MemoryLifecycleStore(lifecycle);
        var service = new CountingValidationService(Result(EmailValidationStatus.Valid, ReasonCode.MailboxAccepted));
        using var metrics = new RevalidationMetrics();
        var processor = new EmailRevalidationProcessor(
            store, service, new StubCoordinator(), new StubDispatcher(true),
            new CoolingThrottle(Now.AddMinutes(20)),
            new RevalidationSchedulePolicy(new StubProviderPolicies(new("Microsoft365", 1, 0, 60, 1)),
                new StubBackoff(Now.AddMinutes(5))),
            metrics, new FixedTimeProvider(Now));

        var disposition = await processor.ProcessAsync(Message(lifecycle.ValidationId, 2));

        Assert.Equal(RevalidationProcessingDisposition.Rescheduled, disposition.Disposition);
        Assert.Equal(0, service.Calls);
        Assert.Equal(Now.AddMinutes(20), store.Value!.NextRetryAt);
    }

    private static ValidationLifecycleCoordinator Coordinator(
        IValidationLifecycleStore store,
        IRevalidationOutboxDispatcher dispatcher,
        IRevalidationMetrics metrics,
        IValidationStatusPublisher? publisher = null) => new(
            store,
            new RevalidationPolicy(
                new StubProviderPolicies(new("Microsoft365", 1, 0, 60, 1)), Options(true)),
            new RevalidationSchedulePolicy(
                new StubProviderPolicies(new("Microsoft365", 1, 0, 60, 1)), new StubBackoff(Now.AddMinutes(5))),
            dispatcher,
            metrics,
            new FixedTimeProvider(Now),
            Options(true),
            NullLogger<ValidationLifecycleCoordinator>.Instance,
            publisher);

    private static IOptions<EmailValidationOptions> Options(bool enabled)
    {
        var options = new EmailValidationOptions();
        options.Revalidation.Enabled = enabled;
        options.Revalidation.DefaultMaxAttempts = 2;
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    private static EmailValidationResult Result(EmailValidationStatus status, ReasonCode reason) => new()
    {
        Email = "person@example.com",
        NormalizedEmail = "person@example.com",
        Status = status,
        Confidence = status == EmailValidationStatus.Unknown ? 0.25 : 0.95,
        Checks = new EmailValidationChecks
        {
            SyntaxValid = true,
            DomainExists = true,
            MxPresent = true,
            Mailbox = status == EmailValidationStatus.Valid ? SmtpMailboxStatus.Accepted : SmtpMailboxStatus.Unknown
        },
        MailProvider = MailProvider.Microsoft365,
        ReasonCodes = [reason],
        DetailedStatus = ValidationSubStatusMapper.Map(new EmailValidationResult
        {
            Email = "person@example.com",
            Status = status,
            Checks = new EmailValidationChecks(),
            ReasonCodes = [reason]
        }),
        Metadata = new(new("1", "2", "3", "4"), Now)
    };

    private static EmailRevalidationMessageV1 Message(string validationId, int attempt) => new(
        validationId, attempt, 2, Now, Now, Now.AddMinutes(5), "Microsoft365",
        EmailValidationStatus.Unknown, DetailedStatus.Unknown, "2");

    private static ValidationLifecycle Lifecycle(ValidationResultState state, int attempt) => new()
    {
        ValidationId = "validation-123",
        NormalizedEmail = "person@example.com",
        Request = new(true),
        ResultState = state,
        AttemptNumber = attempt,
        MaximumAttempts = 2,
        CurrentResult = Result(state == ValidationResultState.Final
            ? EmailValidationStatus.Valid : EmailValidationStatus.Unknown,
            state == ValidationResultState.Final ? ReasonCode.MailboxAccepted : ReasonCode.TemporaryFailure),
        FirstValidatedAt = Now,
        LastValidatedAt = Now,
        NextRetryAt = Now.AddMinutes(5),
        Version = 1
    };

    private sealed class StubProviderPolicies(ProviderPolicy policy) : IProviderPolicyResolver
    {
        public ProviderPolicy Resolve(MailProvider provider) => policy;
    }

    private sealed class StubBackoff(DateTimeOffset at) : IDomainBackoffPolicy
    {
        public DomainBackoffDecision Evaluate(MailProvider provider, SmtpResponseCategory category,
            int consecutiveTemporaryFailures, DateTimeOffset now) => new(at, at - now);
    }

    private sealed class StubDispatcher(bool succeeds) : IRevalidationOutboxDispatcher
    {
        public Task<RevalidationScheduleResult?> DispatchAsync(string validationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RevalidationScheduleResult?>(new(succeeds, $"{validationId}:2", Now.AddMinutes(5)));
        public Task<int> DispatchPendingAsync(int maximumCount, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class MemoryLifecycleStore : IValidationLifecycleStore
    {
        public MemoryLifecycleStore(ValidationLifecycle? value = null) => Value = value;
        public ValidationLifecycle? Value { get; set; }
        public Task<ValidationLifecycle?> GetAsync(string validationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Value?.ValidationId == validationId ? Value : null);
        public Task<ValidationLifecycle?> GetActiveByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(Value?.ResultState == ValidationResultState.Provisional ? Value : null);
        public Task<LifecycleWriteResult> TrySaveAsync(ValidationLifecycle lifecycle, long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            if ((Value?.Version ?? 0) != expectedVersion) return Task.FromResult(new LifecycleWriteResult(false, null));
            Value = lifecycle;
            return Task.FromResult(new LifecycleWriteResult(true, lifecycle));
        }
    }

    private sealed class CountingValidationService(EmailValidationResult result) : IEmailValidationService
    {
        public int Calls { get; private set; }
        public Task<EmailValidationResult> ValidateAsync(string email, EmailValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingValidationService : IEmailValidationService
    {
        public Task<EmailValidationResult> ValidateAsync(
            string email,
            EmailValidationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("internal failure that must not be exposed");
    }

    private sealed class RecordingPublisher(MemoryLifecycleStore store) : IValidationStatusPublisher
    {
        public List<ValidationStatusChanged> Events { get; } = [];
        public bool EveryEventWasAlreadyPersisted { get; private set; } = true;

        public Task PublishAsync(ValidationStatusChanged status, CancellationToken cancellationToken = default)
        {
            Events.Add(status);
            EveryEventWasAlreadyPersisted &= store.Value?.Sequence >= status.Sequence;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPublisher : IValidationStatusPublisher
    {
        public Task PublishAsync(ValidationStatusChanged status, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("status transport unavailable");
    }

    private sealed class StubCoordinator : IValidationLifecycleCoordinator
    {
        public Task<ValidationLifecycleResult> ProcessInitialResultAsync(EmailValidationResult result,
            EmailValidationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationLifecycleResult(result, null, false, false));
        public Task<ValidationLifecycleResult> ProcessRetryResultAsync(string validationId, long expectedVersion,
            int expectedAttemptNumber, EmailValidationResult result, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationLifecycleResult(result, null, true, false));
    }

    private sealed class AvailableThrottle : ISmtpProbeThrottle
    {
        public ValueTask<ISmtpThrottleLease> AcquireAsync(SmtpThrottleContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CoolingThrottle(DateTimeOffset retryAfter) : ISmtpProbeThrottle
    {
        public ProviderThrottleAvailability GetAvailability(SmtpThrottleContext context) =>
            new(false, retryAfter, "ProviderCooldown");
        public ValueTask<ISmtpThrottleLease> AcquireAsync(SmtpThrottleContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
