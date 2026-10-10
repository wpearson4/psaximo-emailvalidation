namespace EmailValidation.Core;

public static class RevalidationMessagePolicy
{
    public static bool IsSupported(EmailRevalidationMessageV1 message) =>
        message.MessageVersion == 1 && message.DispatchGeneration == 0 ||
        message.MessageVersion == 2 && message.DispatchGeneration > 0;
}

internal sealed class RevalidationExecutionHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _work;
    private readonly Task _renewal;
    private int _lost;
    public CancellationToken Token => _work.Token;
    public bool LeaseLost => Volatile.Read(ref _lost) != 0;

    public RevalidationExecutionHeartbeat(IValidationLifecycleStore store, string validationId,
        RevalidationExecutionLease lease, RevalidationOptions options, TimeProvider clock, CancellationToken cancellationToken)
    {
        _work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _renewal = RenewAsync();

        async Task RenewAsync()
        {
            var expires = lease.ExpiresAt;
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.ExecutionRenewalSeconds), clock, _stop.Token).ConfigureAwait(false);
                    var remaining = expires - clock.GetUtcNow();
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                    var now = clock.GetUtcNow();
                    var renewed = await store.RenewExecutionAsync(validationId, lease,
                        TimeSpan.FromSeconds(options.ExecutionLeaseSeconds), _stop.Token)
                        .WaitAsync(remaining, clock, _stop.Token).ConfigureAwait(false);
                    if (!renewed) throw new TimeoutException();
                    expires = now.AddSeconds(options.ExecutionLeaseSeconds);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception)
            {
                Interlocked.Exchange(ref _lost, 1);
                await _work.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _renewal.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
        _work.Dispose();
    }
}

public static class RevalidationExecution
{
    public static bool Owns(ValidationLifecycle lifecycle, RevalidationExecutionLease lease, DateTimeOffset now) =>
        lifecycle.LifecycleState == ValidationLifecycleState.Revalidating &&
        lifecycle.ExecutionLease is { } current && current.OwnerId == lease.OwnerId &&
        current.FencingToken == lease.FencingToken && current.ExpiresAt > now;

    public static ValidationLifecycle? Acquire(ValidationLifecycle current, int attemptNumber,
        string ownerId, TimeSpan duration, DateTimeOffset now)
    {
        if (duration <= TimeSpan.Zero || string.IsNullOrWhiteSpace(ownerId) ||
            current.ResultState != ValidationResultState.Provisional ||
            current.LifecycleState == ValidationLifecycleState.Revalidating ||
            current.ExecutionLease?.ExpiresAt > now ||
            attemptNumber != current.AttemptNumber + 1 || attemptNumber > current.MaximumAttempts)
            return null;
        var fence = checked(current.ExecutionFence + 1);
        return current with
        {
            ExecutionFence = fence,
            ExecutionLease = new(ownerId, fence, now.Add(duration)),
            LifecycleState = ValidationLifecycleState.Revalidating,
            CurrentStage = ValidationProgressStage.Revalidating,
            AttemptNumber = attemptNumber,
            RetryScheduled = false,
            PendingRevalidation = null,
            CurrentResult = current.CurrentResult with { AttemptNumber = attemptNumber, RetryScheduled = false },
            StatusMessage = "Automatic revalidation started.",
            LastUpdatedAt = now,
            Sequence = current.Sequence + 1,
            Version = current.Version + 1
        };
    }
}
