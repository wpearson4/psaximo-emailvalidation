using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class DomainSchedulingTests
{
    [Fact]
    public async Task Scheduler_IsFairAcrossGroupedDomains_AndReturnsOriginalOrder()
    {
        var validator = new TrackingValidator(delayMilliseconds: 2);
        var scheduler = Scheduler(validator, global: 3, perDomain: 1);
        var emails = Enumerable.Range(0, 100).Select(index => $"a{index}@example.com")
            .Concat(["one@smallcorp.com", "two@smallcorp.com"])
            .Concat(Enumerable.Range(0, 5).Select(index => $"b{index}@another.com"))
            .ToArray();
        var work = emails.Select((email, index) => new ValidationWorkItem(
            index, email, new EmailValidationRequest())).ToArray();

        var results = await scheduler.ScheduleAsync(work);

        Assert.Equal(emails, results.Select(result => result.Result.Email));
        Assert.True(validator.StartOrder.IndexOf("one@smallcorp.com") < 10);
        Assert.True(validator.StartOrder.IndexOf("b0@another.com") < 10);
        Assert.Equal(3, scheduler.GetSnapshot().UniqueDomains);
    }

    [Fact]
    public async Task Scheduler_EnforcesPerDomainConcurrency_WhileAllowingCrossDomainConcurrency()
    {
        var validator = new TrackingValidator(delayMilliseconds: 15);
        var scheduler = Scheduler(validator, global: 4, perDomain: 1);
        var work = Enumerable.Range(0, 4)
            .SelectMany(index => new[] { $"a{index}@one.test", $"b{index}@two.test", $"c{index}@three.test", $"d{index}@four.test" })
            .Select((email, index) => new ValidationWorkItem(index, email, new EmailValidationRequest()))
            .ToArray();

        await scheduler.ScheduleAsync(work);

        Assert.All(validator.MaximumByDomain.Values, maximum => Assert.Equal(1, maximum));
        Assert.Equal(4, validator.MaximumGlobal);
    }

    [Fact]
    public async Task StreamingScheduler_YieldsCompletedRowsWithoutWaitingForPendingRows()
    {
        var validator = new ControlledValidator();
        var scheduler = Scheduler(validator, global: 2, perDomain: 1);
        var work = new[]
        {
            new ValidationWorkItem(0, "held@cooling.test", new EmailValidationRequest()),
            new ValidationWorkItem(1, "ready@yahoo.test", new EmailValidationRequest())
        };

        await using var results = scheduler.ScheduleStreamingAsync(work).GetAsyncEnumerator();
        Assert.True(await results.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, results.Current.Sequence);
        validator.Release();
        Assert.True(await results.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, results.Current.Sequence);
        Assert.False(await results.MoveNextAsync());
    }

    [Fact]
    public async Task StreamingScheduler_EarlyReaderDisposal_CancelsBlockedBoundedProducer()
    {
        var scheduler = Scheduler(new TrackingValidator(delayMilliseconds: 1), global: 1, perDomain: 1);
        var work = Enumerable.Range(0, 100)
            .Select(index => new ValidationWorkItem(
                index, $"person{index}@example.test", new EmailValidationRequest()))
            .ToArray();
        var results = scheduler.ScheduleStreamingAsync(work).GetAsyncEnumerator();

        Assert.True(await results.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        await results.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void BackoffPolicy_IsBoundedAndDeterministic()
    {
        var options = Options.Create(new EmailValidationOptions
        {
            Scheduling = new SchedulingOptions
            {
                TemporaryFailureBackoffMilliseconds = 1000,
                MaximumBackoffMilliseconds = 4000
            }
        });
        var policy = new DomainBackoffPolicy(options);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(1), policy.Evaluate(
            MailProvider.Microsoft365, SmtpResponseCategory.RateLimited, 1, now).Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.Evaluate(
            MailProvider.Microsoft365, SmtpResponseCategory.RateLimited, 2, now).Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(4), policy.Evaluate(
            MailProvider.Microsoft365, SmtpResponseCategory.RateLimited, 20, now).Cooldown);
        Assert.Equal(TimeSpan.Zero, policy.Evaluate(
            MailProvider.Microsoft365, SmtpResponseCategory.RecipientRejected, 1, now).Cooldown);
    }

    [Fact]
    public async Task DomainCooldown_DoesNotBlockAReadyDomain()
    {
        var settings = new EmailValidationOptions
        {
            Smtp = new SmtpOptions
            {
                GlobalConcurrency = 2,
                PerDomainConcurrency = 1,
                PerProviderConcurrency = 2,
                DelayBetweenDomainRequestsMilliseconds = 0
            },
            Scheduling = new SchedulingOptions
            {
                DomainMinIntervalMilliseconds = 0,
                DomainIntervalJitterMilliseconds = 0,
                TemporaryFailureBackoffMilliseconds = 5000,
                MaximumBackoffMilliseconds = 5000
            }
        };
        using var throttle = new DomainSmtpProbeThrottle(Options.Create(settings));
        var cooling = new SmtpThrottleContext("cooling.test", "mx.cooling.test", MailProvider.Microsoft365);
        var ready = new SmtpThrottleContext("ready.test", "mx.ready.test", MailProvider.GenericSmtp);
        await using (var initial = await throttle.AcquireAsync(cooling)) { }
        throttle.RecordOutcome(cooling, TemporaryResult());
        using var cancellation = new CancellationTokenSource();

        var coolingTask = throttle.AcquireAsync(cooling, cancellation.Token).AsTask();
        var readyLease = await throttle.AcquireAsync(ready).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(coolingTask.IsCompleted);
        await readyLease.DisposeAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coolingTask);
    }

    private static DomainValidationScheduler Scheduler(IEmailValidator validator, int global, int perDomain)
    {
        var options = Options.Create(new EmailValidationOptions
        {
            Scheduling = new SchedulingOptions
            {
                GlobalConcurrency = global,
                PerDomainConcurrency = perDomain,
                MaxActiveDomains = 1000
            }
        });
        return new(validator, new EmailNormalizer(), options, NullLogger<DomainValidationScheduler>.Instance);
    }

    private static SmtpProbeResult TemporaryResult()
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.Connect, null, null, SmtpResponseCategory.Timeout,
            SmtpResponseTextClassification.TemporaryCondition, 1, MailProvider.Microsoft365,
            "mx.cooling.test", 1, DateTimeOffset.UtcNow, "connection timed out");
        return new(SmtpMailboxStatus.Timeout, null, "connection timed out", TimeSpan.Zero,
            Evidence: evidence);
    }

    private sealed class TrackingValidator(int delayMilliseconds) : IEmailValidator
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, int> _activeByDomain = new(StringComparer.OrdinalIgnoreCase);
        private int _activeGlobal;
        public List<string> StartOrder { get; } = [];
        public Dictionary<string, int> MaximumByDomain { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int MaximumGlobal { get; private set; }

        public async Task<EmailValidationResult> ValidateAsync(
            string email,
            EmailValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            var domain = email[(email.LastIndexOf('@') + 1)..];
            lock (_sync)
            {
                StartOrder.Add(email);
                _activeGlobal++;
                _activeByDomain.TryGetValue(domain, out var active);
                _activeByDomain[domain] = ++active;
                MaximumGlobal = Math.Max(MaximumGlobal, _activeGlobal);
                MaximumByDomain[domain] = Math.Max(MaximumByDomain.GetValueOrDefault(domain), active);
            }
            await Task.Delay(delayMilliseconds, cancellationToken);
            lock (_sync)
            {
                _activeGlobal--;
                _activeByDomain[domain]--;
            }
            return Result(email);
        }

        private static EmailValidationResult Result(string email) => new()
        {
            Email = email,
            NormalizedEmail = email,
            Status = EmailValidationStatus.Valid,
            Confidence = 1,
            Checks = new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true }
        };
    }

    private sealed class ControlledValidator : IEmailValidator
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<EmailValidationResult> ValidateAsync(
            string email,
            EmailValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (email.StartsWith("held", StringComparison.Ordinal))
                await _release.Task.WaitAsync(cancellationToken);
            return new EmailValidationResult
            {
                Email = email,
                NormalizedEmail = email,
                Status = EmailValidationStatus.Valid,
                Confidence = 1,
                Checks = new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true }
            };
        }

        public void Release() => _release.TrySetResult();
    }
}
