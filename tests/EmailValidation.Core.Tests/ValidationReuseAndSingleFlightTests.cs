using System.Collections.Concurrent;
using System.Text;
using EmailValidation.ConsoleApp;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class ValidationReuseAndSingleFlightTests
{
    private static readonly ValidationPolicyVersions Policy = new("1.1.0", "2.2.0", "3.1.0", "1.1.0");
    private static readonly string[] DistinctEmails =
        ["one@example.test", "two@example.test", "three@example.test"];

    [Fact]
    public async Task FreshLiveResult_PopulatesMemoryAndAvoidsPersistenceOnEquivalentRequest()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var store = new TrackingStore();
        var executor = new ImmediateExecutor(clock);
        var (validator, metrics) = CreateValidator(executor, store, clock);

        var first = await validator.ValidateAsync(" Person@Example.Test ", new EmailValidationRequest(true));
        var readsAfterLive = store.Reads;
        var second = await validator.ValidateAsync("person@example.test", new EmailValidationRequest(true));

        Assert.Equal(1, executor.Calls);
        Assert.Equal(readsAfterLive, store.Reads);
        Assert.Equal(ValidationResultSource.LiveValidation, first.Metadata!.ResultSource);
        Assert.Equal(ValidationResultSource.MemoryCache, second.Metadata!.ResultSource);
        Assert.Equal(first.Metadata.ValidatedAt, second.Metadata.ValidatedAt);
        Assert.Equal("person@example.test", store.Mailbox!.LastResult.Email);
        Assert.Equal(1, metrics.GetSnapshot().MemoryCacheHits);
    }

    [Fact]
    public async Task FreshPersistentResult_WarmsMemoryAndPreservesOriginalValidationTime()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var original = clock.GetUtcNow().AddMinutes(-10);
        var domain = Domain(clock, "example.test");
        var storedResult = Result("person@example.test", original, domain);
        var store = new TrackingStore
        {
            Domain = domain,
            Mailbox = Mailbox(storedResult, original)
        };
        var executor = new ImmediateExecutor(clock);
        var (validator, metrics) = CreateValidator(executor, store, clock);

        var persistent = await validator.ValidateAsync("person@example.test", new EmailValidationRequest(true));
        var readsAfterPersistentHit = store.Reads;
        var memory = await validator.ValidateAsync("person@example.test", new EmailValidationRequest(true));

        Assert.Equal(0, executor.Calls);
        Assert.Equal(readsAfterPersistentHit, store.Reads);
        Assert.Equal(ValidationResultSource.PersistentReuse, persistent.Metadata!.ResultSource);
        Assert.Equal(ValidationResultSource.MemoryCache, memory.Metadata!.ResultSource);
        Assert.Equal(original, persistent.Metadata.OriginalValidatedAt);
        Assert.Equal(clock.GetUtcNow(), persistent.Metadata.ReturnedAt);
        Assert.Equal(TimeSpan.FromMinutes(10), persistent.Metadata.ReuseAge);
        Assert.Equal(1, metrics.GetSnapshot().PersistentReuseHits);
    }

    [Fact]
    public async Task StaleMailbox_WithFreshDomain_ExecutesLiveMailboxValidation()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var domain = Domain(clock, "example.test");
        var old = clock.GetUtcNow().AddDays(-1);
        var store = new TrackingStore
        {
            Domain = domain,
            Mailbox = Mailbox(Result("person@example.test", old, domain), old)
        };
        var executor = new ImmediateExecutor(clock);
        var (validator, metrics) = CreateValidator(executor, store, clock);

        var result = await validator.ValidateAsync("person@example.test", new EmailValidationRequest(true));
        var snapshot = metrics.GetSnapshot();

        Assert.Equal(1, executor.Calls);
        Assert.Equal(ValidationResultSource.LiveValidation, result.Metadata!.ResultSource);
        Assert.Equal(1, snapshot.StaleMailboxRefreshes);
        Assert.Equal(1, snapshot.DomainReuses);
        Assert.Equal(1, snapshot.StaleRejections);
    }

    [Fact]
    public async Task ConcurrentEquivalentRequests_CollapseToOneLiveExecution()
    {
        const int callerCount = 20;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var executor = new BlockingExecutor(clock);
        var entries = new EntryCountingSingleFlight(callerCount);
        var (validator, metrics) = CreateValidator(executor, new TrackingStore(), clock, singleFlight: entries);

        var tasks = Enumerable.Range(0, callerCount)
            .Select(index => validator.ValidateAsync(
                index % 2 == 0 ? " Person@Example.Test " : "person@example.test",
                new EmailValidationRequest(true)))
            .ToArray();
        await entries.AllEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, executor.Calls);
        executor.Release();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, executor.Calls);
        Assert.Single(results, result => result.Metadata!.ResultSource == ValidationResultSource.LiveValidation);
        Assert.Equal(callerCount - 1, results.Count(result =>
            result.Metadata!.ResultSource == ValidationResultSource.JoinedInFlightValidation));
        Assert.Equal(callerCount - 1, metrics.GetSnapshot().SingleFlightJoiners);
        Assert.Equal(0.95, metrics.GetSnapshot().SingleFlightCollapseRatio, precision: 2);
    }

    [Fact]
    public async Task DifferentNormalizedEmails_DoNotShareAFlight()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var executor = new BlockingExecutor(clock);
        var entries = new EntryCountingSingleFlight(3);
        var (validator, _) = CreateValidator(executor, new TrackingStore(), clock, singleFlight: entries);

        var tasks = DistinctEmails
            .Select(email => validator.ValidateAsync(email, new EmailValidationRequest(true)))
            .ToArray();
        await entries.AllEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, executor.Calls);
        executor.Release();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task CancellingOneWaiter_DoesNotCancelTheSharedOperation()
    {
        var singleFlight = new ValidationSingleFlight();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<EmailValidationResult> Factory(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(token);
            return Result("person@example.test", DateTimeOffset.UtcNow, null);
        }

        var leader = singleFlight.ExecuteAsync("person@example.test", Factory);
        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = singleFlight.ExecuteAsync("person@example.test", Factory, cancellation.Token);
        var remainingWaiter = singleFlight.ExecuteAsync("person@example.test", Factory);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);
        Assert.False(leader.IsCompleted);
        release.SetResult();
        await Task.WhenAll(leader, remainingWaiter);
        Assert.Equal(1, calls);
        Assert.Equal(0, singleFlight.ActiveCount);
    }

    [Fact]
    public async Task FailedFlight_IsRemovedAndCanBeRetried()
    {
        var singleFlight = new ValidationSingleFlight();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<EmailValidationResult> Fail(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(token);
            throw new IOException("simulated live failure");
        }

        var first = singleFlight.ExecuteAsync("person@example.test", Fail);
        var joined = singleFlight.ExecuteAsync("person@example.test", Fail);
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<IOException>(() => joined);
        Assert.Equal(1, calls);
        Assert.Equal(0, singleFlight.ActiveCount);

        var retry = await singleFlight.ExecuteAsync(
            "person@example.test",
            _ => Task.FromResult(Result("person@example.test", DateTimeOffset.UtcNow, null)));
        Assert.Equal(EmailValidationStatus.LikelyValid, retry.Status);
    }

    [Fact]
    public async Task SynchronouslyThrowingFactory_IsRemovedAndCanBeRetried()
    {
        var singleFlight = new ValidationSingleFlight();

        await Assert.ThrowsAsync<IOException>(() => singleFlight.ExecuteAsync(
            "person@example.test",
            _ => throw new IOException("synchronous factory failure")));

        Assert.Equal(0, singleFlight.ActiveCount);
        var retry = await singleFlight.ExecuteAsync(
            "person@example.test",
            _ => Task.FromResult(Result("person@example.test", DateTimeOffset.UtcNow, null)));
        Assert.Equal(EmailValidationStatus.LikelyValid, retry.Status);
    }

    [Fact]
    public void ReusePolicy_RejectsPolicyAndTopologyChanges()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new EmailValidationOptions());
        var policy = new ValidationResultReusePolicy(options);
        var domain = Domain(clock, "example.test");
        var mailbox = Mailbox(Result("person@example.test", clock.GetUtcNow(), domain), clock.GetUtcNow());

        var versionDecision = policy.Evaluate(
            mailbox, domain, new EmailValidationRequest(true),
            Policy with { ClassificationPolicyVersion = "next" }, clock.GetUtcNow());
        var topologyDecision = policy.Evaluate(
            mailbox,
            domain with { Provider = domain.Provider with { TopologyFingerprint = "changed" } },
            new EmailValidationRequest(true), Policy, clock.GetUtcNow());

        Assert.Equal(ValidationReuseRejectionReason.PolicyVersion, versionDecision.RejectionReason);
        Assert.Equal(ValidationReuseAction.CannotReuse, versionDecision.Action);
        Assert.Equal(ValidationReuseRejectionReason.MxTopology, topologyDecision.RejectionReason);
        Assert.Equal(ValidationReuseAction.RevalidateMailboxOnly, topologyDecision.Action);
    }

    [Fact]
    public void RecentTransientBlock_IsReusableOnlyForShortConfiguredWindow()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new EmailValidationOptions
        {
            ResultReuse = new ResultReuseOptions { TransientMinutes = 2 }
        });
        var policy = new ValidationResultReusePolicy(options);
        var domain = Domain(clock, "example.test");
        var blockedResult = Result(
            "person@example.test", clock.GetUtcNow(), domain,
            EmailValidationStatus.Unknown, SmtpMailboxStatus.Blocked,
            [ReasonCode.ProviderVerificationBlocked]);
        var mailbox = Mailbox(blockedResult, clock.GetUtcNow()) with
        {
            PreviousStatus = EmailValidationStatus.Unknown,
            PreviousMailboxResult = SmtpMailboxStatus.Blocked,
            ReasonCodes = [ReasonCode.ProviderVerificationBlocked]
        };

        Assert.True(policy.Evaluate(
            mailbox, domain, new EmailValidationRequest(true), Policy, clock.GetUtcNow()).CanReuse);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False(policy.Evaluate(
            mailbox, domain, new EmailValidationRequest(true), Policy, clock.GetUtcNow()).CanReuse);
    }

    [Fact]
    public async Task MemoryCache_IsBoundedAndExpiresEntries()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new EmailValidationOptions
        {
            ResultReuse = new ResultReuseOptions { MemoryCacheSizeLimit = 2 }
        });
        var cache = new InMemoryValidationResultCache(options, clock);
        var result = Result("person@example.test", clock.GetUtcNow(), null);

        await cache.SetAsync("one", result, TimeSpan.FromMinutes(5));
        await cache.SetAsync("two", result, TimeSpan.FromMinutes(5));
        await cache.SetAsync("three", result, TimeSpan.FromMinutes(5));
        Assert.Null(await cache.GetAsync("one"));
        Assert.NotNull(await cache.GetAsync("two"));
        Assert.Equal(2, cache.Count);

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(await cache.GetAsync("two"));
        Assert.Null(await cache.GetAsync("three"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task PersistenceUnavailable_StillCollapsesConcurrentLiveRequests()
    {
        const int callerCount = 12;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var executor = new BlockingExecutor(clock);
        var entries = new EntryCountingSingleFlight(callerCount);
        var store = new TrackingStore { Unavailable = true };
        var (validator, _) = CreateValidator(executor, store, clock, singleFlight: entries);

        var tasks = Enumerable.Range(0, callerCount)
            .Select(_ => validator.ValidateAsync("person@example.test", new EmailValidationRequest(true)))
            .ToArray();
        await entries.AllEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, executor.Calls);
        executor.Release();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task CacheFailure_IsIsolatedFromSuccessfulLiveValidation()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var executor = new ImmediateExecutor(clock);
        var (validator, _) = CreateValidator(
            executor, new TrackingStore(), clock, cache: new ThrowingCache());

        var result = await validator.ValidateAsync("person@example.test", new EmailValidationRequest(true));

        Assert.Equal(1, executor.Calls);
        Assert.Equal(EmailValidationStatus.LikelyValid, result.Status);
        Assert.Equal(ValidationResultSource.LiveValidation, result.Metadata!.ResultSource);
    }

    [Fact]
    public async Task UnexpectedLiveException_IsNotCachedAndNextRequestRetries()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var executor = new FailOnceExecutor(clock);
        var (validator, _) = CreateValidator(executor, new TrackingStore(), clock);

        await Assert.ThrowsAsync<IOException>(() =>
            validator.ValidateAsync("person@example.test", new EmailValidationRequest(true)));
        var retry = await validator.ValidateAsync("person@example.test", new EmailValidationRequest(true));

        Assert.Equal(2, executor.Calls);
        Assert.Equal(EmailValidationStatus.LikelyValid, retry.Status);
        Assert.Equal(ValidationResultSource.LiveValidation, retry.Metadata!.ResultSource);
    }

    [Fact]
    public async Task CsvDuplicates_ShareValidationAndPreserveRowOrder()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var executor = new BlockingExecutor(clock);
        var entries = new EntryCountingSingleFlight(4);
        var (validator, _) = CreateValidator(executor, new TrackingStore(), clock, singleFlight: entries);
        var options = Options.Create(new EmailValidationOptions
        {
            Smtp = new SmtpOptions { GlobalConcurrency = 4, PerDomainConcurrency = 4 },
            Scheduling = new SchedulingOptions { GlobalConcurrency = 4, PerDomainConcurrency = 4 }
        });
        var processor = new CsvFileProcessor(validator, options, NullLogger<CsvFileProcessor>.Instance);
        var directory = Path.Combine(Path.GetTempPath(), $"email-reuse-csv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "contacts.csv");
        await File.WriteAllTextAsync(
            path,
            "Email\njohn@example.test\njane@example.test\njohn@example.test\njohn@example.test\n",
            new UTF8Encoding(false));
        try
        {
            var processing = processor.ProcessAsync(path, null, true, false, TextWriter.Null, default);
            await entries.AllEntered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, executor.CallsFor("john@example.test"));
            executor.Release();
            await processing;

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Contains("john@example.test", lines[1], StringComparison.Ordinal);
            Assert.Contains("jane@example.test", lines[2], StringComparison.Ordinal);
            Assert.Contains("john@example.test", lines[3], StringComparison.Ordinal);
            Assert.Contains("john@example.test", lines[4], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (IntelligenceEmailValidator Validator, ValidationPersistenceMetrics Metrics) CreateValidator(
        IEmailValidationExecutor executor,
        IValidationIntelligenceStore store,
        TimeProvider clock,
        IValidationSingleFlight? singleFlight = null,
        IValidationResultCache? cache = null,
        EmailValidationOptions? configuration = null)
    {
        var options = Options.Create(configuration ?? new EmailValidationOptions());
        var metrics = new ValidationPersistenceMetrics();
        var validator = new IntelligenceEmailValidator(
            executor,
            new EmailNormalizer(),
            store,
            cache ?? new InMemoryValidationResultCache(options, clock),
            singleFlight ?? new ValidationSingleFlight(),
            new ValidationResultReusePolicy(options),
            new EmailRiskIntelligence([new ExistingIntelligenceRiskDataSource()]),
            new ValidationQualityMetrics(),
            metrics,
            options,
            clock,
            NullLogger<IntelligenceEmailValidator>.Instance);
        return (validator, metrics);
    }

    private static DomainIntelligence Domain(TimeProvider clock, string domain) => new()
    {
        Domain = domain,
        DomainExists = true,
        Dns = new DnsLookupResult(
            DnsStatus.Success, true, [new MxRecord(10, $"mx.{domain}")], false, TimeSpan.Zero),
        Provider = new ProviderDetectionResult(
            MailProvider.GenericSmtp, 0.9, TopologyFingerprint: $"topology:{domain}"),
        CatchAll = new CatchAllDetectionResult(CatchAllStatus.NotCatchAll, 1, 0, 1, 0, Confidence: 0.9),
        ObservedAt = clock.GetUtcNow(),
        EvidenceExpiresAt = clock.GetUtcNow().AddHours(1),
        StrategyVersion = Policy.ProviderStrategyVersion
    };

    private static EmailValidationResult Result(
        string email,
        DateTimeOffset validatedAt,
        DomainIntelligence? domain,
        EmailValidationStatus status = EmailValidationStatus.LikelyValid,
        SmtpMailboxStatus mailbox = SmtpMailboxStatus.Accepted,
        IReadOnlyList<ReasonCode>? reasons = null) => new()
        {
            Email = email,
            NormalizedEmail = email.Trim().ToLowerInvariant(),
            Status = status,
            Confidence = status == EmailValidationStatus.Unknown ? 0.4 : 0.95,
            Checks = new EmailValidationChecks
            {
                SyntaxValid = true,
                DomainExists = true,
                MxPresent = true,
                Mailbox = mailbox,
                CatchAll = CatchAllStatus.NotCatchAll
            },
            MailProvider = MailProvider.GenericSmtp,
            Provider = domain?.Provider,
            DomainIntelligence = domain,
            ReasonCodes = reasons ?? [ReasonCode.MailboxAccepted],
            Metadata = new ValidationResultMetadata(
                Policy,
                validatedAt,
                MxTopologyFingerprint: domain?.Provider.TopologyFingerprint)
        };

    private static MailboxIntelligence Mailbox(EmailValidationResult result, DateTimeOffset validatedAt) => new()
    {
        NormalizedEmail = result.NormalizedEmail!,
        PreviousStatus = result.Status,
        PreviousMailboxResult = result.Checks.Mailbox,
        PreviousConfidence = result.Confidence,
        PreviousConfidenceType = result.ConfidenceType,
        LastValidatedAt = validatedAt,
        LastStrongPositiveEvidenceAt = result.Status is EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid
            ? validatedAt
            : null,
        LastStrongNegativeEvidenceAt = result.Status is EmailValidationStatus.Invalid or EmailValidationStatus.LikelyInvalid
            ? validatedAt
            : null,
        ProviderAtValidation = result.MailProvider,
        Policy = Policy,
        ReasonCodes = result.ReasonCodes,
        MxTopologyFingerprint = result.DomainIntelligence?.Provider.TopologyFingerprint,
        UsedLiveSmtp = true,
        LastResult = result
    };

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }

    private sealed class TrackingStore : IValidationIntelligenceStore
    {
        private int _reads;
        public DomainIntelligence? Domain { get; set; }
        public MailboxIntelligence? Mailbox { get; set; }
        public bool Unavailable { get; init; }
        public int Reads => Volatile.Read(ref _reads);

        public Task<DomainIntelligence?> GetDomainAsync(string domain, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(Unavailable ? null : Domain);
        }

        public Task<MailboxIntelligence?> GetMailboxAsync(
            string normalizedEmail,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(Unavailable ? null : Mailbox);
        }

        public Task SaveDomainAsync(DomainIntelligence intelligence, CancellationToken cancellationToken = default)
        {
            if (!Unavailable) Domain = intelligence;
            return Task.CompletedTask;
        }

        public Task SaveMailboxAsync(MailboxIntelligence intelligence, CancellationToken cancellationToken = default)
        {
            if (!Unavailable) Mailbox = intelligence;
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateExecutor(TimeProvider clock) : IEmailValidationExecutor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<EmailValidationResult> ValidateAsync(
            string email,
            EmailValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var normalized = new EmailNormalizer().Normalize(email);
            var domain = Domain(clock, normalized.Domain!);
            return Task.FromResult(Result(normalized.NormalizedEmail!, clock.GetUtcNow(), domain));
        }
    }

    private sealed class BlockingExecutor(TimeProvider clock) : IEmailValidationExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, int> _callsByEmail = new(StringComparer.OrdinalIgnoreCase);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public int CallsFor(string email) => _callsByEmail.TryGetValue(email, out var count) ? count : 0;

        public async Task<EmailValidationResult> ValidateAsync(
            string email,
            EmailValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var normalized = new EmailNormalizer().Normalize(email);
            _callsByEmail.AddOrUpdate(normalized.NormalizedEmail!, 1, (_, count) => count + 1);
            await _release.Task.WaitAsync(cancellationToken);
            var domain = Domain(clock, normalized.Domain!);
            return Result(normalized.NormalizedEmail!, clock.GetUtcNow(), domain);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class FailOnceExecutor(TimeProvider clock) : IEmailValidationExecutor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<EmailValidationResult> ValidateAsync(
            string email,
            EmailValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new IOException("simulated unexpected live failure");
            var normalized = new EmailNormalizer().Normalize(email);
            var domain = Domain(clock, normalized.Domain!);
            return Task.FromResult(Result(normalized.NormalizedEmail!, clock.GetUtcNow(), domain));
        }
    }

    private sealed class EntryCountingSingleFlight(int expectedEntries) : IValidationSingleFlight
    {
        private readonly ValidationSingleFlight _inner = new();
        private readonly TaskCompletionSource _allEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entries;
        public Task AllEntered => _allEntered.Task;

        public async Task<EmailValidationResult> ExecuteAsync(
            string key,
            Func<CancellationToken, Task<EmailValidationResult>> factory,
            CancellationToken cancellationToken = default)
        {
            var execution = await ExecuteWithStatusAsync(key, factory, cancellationToken);
            return execution.Result;
        }

        public Task<ValidationSingleFlightResult> ExecuteWithStatusAsync(
            string key,
            Func<CancellationToken, Task<EmailValidationResult>> factory,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _entries) == expectedEntries) _allEntered.TrySetResult();
            return _inner.ExecuteWithStatusAsync(key, factory, cancellationToken);
        }
    }

    private sealed class ThrowingCache : IValidationResultCache
    {
        public Task<EmailValidationResult?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            throw new IOException("simulated cache read failure");
        public Task SetAsync(
            string key,
            EmailValidationResult result,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default) =>
            throw new IOException("simulated cache write failure");
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            throw new IOException("simulated cache invalidation failure");
    }
}
