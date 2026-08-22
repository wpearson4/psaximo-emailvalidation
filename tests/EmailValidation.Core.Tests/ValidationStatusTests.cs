using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailValidation.Core.Tests;

public sealed class ValidationStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 14, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task LateSubscriber_ImmediatelyReceivesCanonicalRetryWaitingSnapshot()
    {
        var store = new InMemoryValidationLifecycleStore();
        var lifecycle = Lifecycle(ValidationLifecycleState.RetryWaiting, 4) with
        {
            RetryScheduled = true,
            NextRetryAt = Now.AddMinutes(45),
            RetryReason = ReasonCode.ProviderVerificationBlocked.ToString()
        };
        Assert.True((await store.TrySaveAsync(lifecycle, 0)).Applied);
        var query = new ValidationStatusQueryService(store);
        using var dispatcher = new InMemoryValidationStatusDispatcher(
            query, NullLogger<InMemoryValidationStatusDispatcher>.Instance, new FixedTimeProvider(Now));

        await using var enumerator = dispatcher.SubscribeAsync(lifecycle.ValidationId).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(ValidationLifecycleState.RetryWaiting, enumerator.Current.LifecycleState);
        Assert.Equal(Now.AddMinutes(45), enumerator.Current.RetryAt);
        Assert.Equal(TimeSpan.FromMinutes(45), enumerator.Current.EstimatedRetryIn);
        Assert.True(enumerator.Current.RetryScheduled);
    }

    [Fact]
    public async Task Subscription_DropsDuplicateAndOutOfOrderSequences()
    {
        var store = new InMemoryValidationLifecycleStore();
        var requested = Lifecycle(ValidationLifecycleState.Requested, 1);
        Assert.True((await store.TrySaveAsync(requested, 0)).Applied);
        using var dispatcher = new InMemoryValidationStatusDispatcher(
            new ValidationStatusQueryService(store), NullLogger<InMemoryValidationStatusDispatcher>.Instance,
            new FixedTimeProvider(Now));
        await using var enumerator = dispatcher.SubscribeAsync(requested.ValidationId).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        var next = enumerator.MoveNextAsync().AsTask();
        await dispatcher.PublishAsync(Event(3, ValidationLifecycleState.Provisional));
        await dispatcher.PublishAsync(Event(2, ValidationLifecycleState.Validating));
        await dispatcher.PublishAsync(Event(3, ValidationLifecycleState.Provisional));
        Assert.True(await next);
        Assert.Equal(3, enumerator.Current.Sequence);

        next = enumerator.MoveNextAsync().AsTask();
        await dispatcher.PublishAsync(Event(4, ValidationLifecycleState.Final));
        Assert.True(await next);
        Assert.Equal(4, enumerator.Current.Sequence);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task CurrentStatusQuery_ReadsCanonicalStoreWithoutLiveDispatcher()
    {
        var store = new InMemoryValidationLifecycleStore();
        var lifecycle = Lifecycle(ValidationLifecycleState.Final, 7) with
        {
            ResultState = ValidationResultState.Final,
            FinalizedAt = Now
        };
        Assert.True((await store.TrySaveAsync(lifecycle, 0)).Applied);

        var snapshot = await new ValidationStatusQueryService(store).GetAsync(lifecycle.ValidationId);

        Assert.NotNull(snapshot);
        Assert.Equal(ValidationLifecycleState.Final, snapshot.LifecycleState);
        Assert.Equal(7, snapshot.Sequence);
        Assert.False(snapshot.IsRunning);
    }

    [Fact]
    public async Task ProgressReporter_PersistsCoarseStageBeforePublishing()
    {
        var store = new InMemoryValidationLifecycleStore();
        var lifecycle = Lifecycle(ValidationLifecycleState.Validating, 2);
        Assert.True((await store.TrySaveAsync(lifecycle, 0)).Applied);
        using var dispatcher = new InMemoryValidationStatusDispatcher(
            new ValidationStatusQueryService(store), NullLogger<InMemoryValidationStatusDispatcher>.Instance,
            new FixedTimeProvider(Now));
        var reporter = new ValidationLifecycleProgressReporter(
            store, dispatcher, new FixedTimeProvider(Now),
            NullLogger<ValidationLifecycleProgressReporter>.Instance);

        await reporter.ReportAsync(
            lifecycle.ValidationId, ValidationProgressStage.DomainChecks, "Domain and MX validation completed.");
        var snapshot = await new ValidationStatusQueryService(store).GetAsync(lifecycle.ValidationId);

        Assert.Equal(ValidationProgressStage.DomainChecks, snapshot!.CurrentStage);
        Assert.Equal(3, snapshot.Sequence);
        Assert.Equal("Domain and MX validation completed.", snapshot.StatusMessage);
    }

    private static ValidationLifecycle Lifecycle(ValidationLifecycleState state, long sequence) => new()
    {
        ValidationId = "validation-123",
        NormalizedEmail = "person@example.test",
        Request = new(true, ValidationId: "validation-123"),
        ResultState = state == ValidationLifecycleState.Final
            ? ValidationResultState.Final
            : ValidationResultState.Provisional,
        AttemptNumber = state is ValidationLifecycleState.Requested or ValidationLifecycleState.Validating ? 0 : 1,
        MaximumAttempts = 2,
        CurrentResult = Result(),
        Attempts = [],
        FirstValidatedAt = Now,
        LastValidatedAt = Now,
        RequestedAt = Now,
        StartedAt = Now,
        LastUpdatedAt = Now,
        LifecycleState = state,
        CurrentStage = state switch
        {
            ValidationLifecycleState.Requested => ValidationProgressStage.Requested,
            ValidationLifecycleState.Validating => ValidationProgressStage.Started,
            ValidationLifecycleState.RetryWaiting => ValidationProgressStage.RetryWaiting,
            ValidationLifecycleState.Final => ValidationProgressStage.Final,
            _ => ValidationProgressStage.Provisional
        },
        Sequence = sequence,
        Version = sequence
    };

    private static ValidationStatusChanged Event(long sequence, ValidationLifecycleState state) => new()
    {
        ValidationId = "validation-123",
        LifecycleState = state,
        ResultState = state == ValidationLifecycleState.Final
            ? ValidationResultState.Final
            : ValidationResultState.Provisional,
        Sequence = sequence,
        OccurredAt = Now
    };

    private static EmailValidationResult Result() => new()
    {
        Email = "person@example.test",
        NormalizedEmail = "person@example.test",
        Status = EmailValidationStatus.Unknown,
        Confidence = 0.25,
        Checks = new EmailValidationChecks(),
        MailProvider = MailProvider.Microsoft365,
        ResultState = ValidationResultState.Provisional,
        AttemptNumber = 1,
        MaximumAttempts = 2
    };

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
