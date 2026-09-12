using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EmailValidation.Core.Tests;

public sealed class ValidationJobTests
{
    [Fact]
    public void MongoOutbox_UsesScalarUtcDatesForIndexedLeaseFields()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var document = MongoValidationJobStore.JobDocument.FromModel(new ValidationJobSnapshot(
            "job-1", now, ValidationJobState.Requested, 1, 0, 0, 0, 0, now,
            DispatchId: "dispatch-1", DispatchChunkCount: 1));
        document.DispatchLeaseExpiresAtUtc = now.AddMinutes(1).UtcDateTime;
        document.DispatchRetryAtUtc = now.AddMinutes(2).UtcDateTime;

        var bson = document.ToBsonDocument();

        Assert.Equal(BsonType.DateTime, bson[nameof(document.DispatchLeaseExpiresAtUtc)].BsonType);
        Assert.Equal(BsonType.DateTime, bson[nameof(document.DispatchRetryAtUtc)].BsonType);
    }

    [Fact]
    public void MongoFinalization_UsesAnExplicitInFilterForActiveStates()
    {
        var serializer = BsonSerializer.LookupSerializer<MongoValidationJobStore.JobDocument>();

        var filter = MongoValidationJobStore.ActiveJobFilter("job-1")
            .Render(new RenderArgs<MongoValidationJobStore.JobDocument>(
                serializer, BsonSerializer.SerializerRegistry));

        Assert.Equal("job-1", filter["_id"].AsString);
        var states = filter[nameof(MongoValidationJobStore.JobDocument.State)]
            .AsBsonDocument["$in"].AsBsonArray;
        Assert.Equal(3, states.Count);
    }

    [Fact]
    public async Task Create_PersistsItemsAndDurableDispatchBeforePublishing()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var dispatcher = new RecordingDispatcher();
        var options = Options();
        var service = new ValidationJobService(store, options, TimeProvider.System);

        var job = await service.CreateAsync(new CreateValidationJobRequest(
            ["one@example.com", "two@example.com"], EnableSmtp: false));

        Assert.Equal(ValidationJobState.Requested, job.State);
        Assert.Equal(ValidationJobDispatchState.Pending, job.DispatchState);
        Assert.False(string.IsNullOrWhiteSpace(job.DispatchId));
        Assert.Equal(1, job.DispatchChunkCount);
        Assert.Equal(2, job.TotalItems);
        Assert.False(job.EnableSmtp);
        Assert.Null(dispatcher.JobId);
        Assert.Equal(1, await Outbox(store, dispatcher, options, TimeProvider.System).DispatchPendingAsync(20));
        job = (await service.GetAsync(job.JobId))!;
        Assert.Equal(ValidationJobState.Queued, job.State);
        Assert.Equal(ValidationJobDispatchState.Published, job.DispatchState);
        Assert.Equal(job.JobId, dispatcher.JobId);
        Assert.Equal(job.DispatchId, dispatcher.DispatchId);
        Assert.Equal(1, dispatcher.MessageCount);
        Assert.Equal(["one@example.com", "two@example.com"],
            (await service.GetResultsAsync(job.JobId, 0, 10)).Select(value => value.Email));
    }

    [Fact]
    public async Task Create_IgnoresBlankEmailsAndPreservesTheirSourceRowPositions()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, Options(), TimeProvider.System);

        var job = await service.CreateAsync(new CreateValidationJobRequest(
            ["one@example.com", " ", "two@example.com"],
            SourcePositions: [4, 5, 6]));

        var items = await service.GetResultsAsync(job.JobId, 0, 10);
        Assert.Equal(2, job.TotalItems);
        Assert.Equal([4, 6], items.Select(item => item.Position));
        Assert.Equal(["one@example.com", "two@example.com"], items.Select(item => item.Email));
    }

    [Fact]
    public async Task Processor_UsesBoundedConcurrencyAndCompletesWithProgress()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, Options(maximumConcurrency: 2), TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(
            Enumerable.Range(0, 7).Select(index => $"person{index}@example.com").ToArray()));
        var validator = new TrackingValidator();
        var processor = Processor(store, validator, Options(maximumConcurrency: 2));

        for (var chunk = 0; chunk < 3; chunk++) await processor.ProcessAsync(job.JobId);

        var completed = await service.GetAsync(job.JobId);
        Assert.Equal(ValidationJobState.Completed, completed!.State);
        Assert.Equal(7, completed.ProcessedItems);
        Assert.Equal(7, completed.FinalItems);
        Assert.InRange(validator.MaximumActive, 1, 2);
    }

    [Fact]
    public async Task Processor_PreservesPartialErrorsAndCompletesWithErrors()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, Options(), TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(["ok@example.com", "fail@example.com"]));
        var processor = Processor(store, new TrackingValidator("fail@example.com"), Options());

        await processor.ProcessAsync(job.JobId);

        var completed = await service.GetAsync(job.JobId);
        var results = await service.GetResultsAsync(job.JobId, 0, 10);
        Assert.Equal(ValidationJobState.CompletedWithErrors, completed!.State);
        Assert.Equal(2, completed.ProcessedItems);
        Assert.Equal(1, completed.FailedItems);
        Assert.Equal(ValidationJobItemState.Failed, results[1].State);
    }

    [Fact]
    public async Task Create_RejectsSourceFileThatAlreadyCompleted()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, Options(), TimeProvider.System);
        var request = new CreateValidationJobRequest(
            ["ok@example.com"], SourceFileId: "source-file-1", SourceFileName: "source.csv");
        var job = await service.CreateAsync(request);
        var processor = Processor(store, new TrackingValidator(), Options());
        await processor.ProcessAsync(job.JobId);

        await Assert.ThrowsAsync<ValidationJobSourceFileCompletedException>(() => service.CreateAsync(request));
    }

    [Fact]
    public async Task Create_RequeuesFailedSourceFileWithoutDuplicatingIt()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var dispatcher = new RecordingDispatcher();
        var options = Options();
        var service = new ValidationJobService(store, options, TimeProvider.System);
        var request = new CreateValidationJobRequest(
            ["ok@example.com"], SourceFileId: "source-file-1", SourceFileName: "source.csv");
        var job = await service.CreateAsync(request);
        var originalDispatchId = job.DispatchId;
        await store.TrySetFailedAsync(job.JobId, "worker failure");

        var retried = await service.CreateAsync(request);

        Assert.Equal(job.JobId, retried.JobId);
        Assert.Equal(ValidationJobState.Requested, retried.State);
        Assert.Equal(ValidationJobDispatchState.Pending, retried.DispatchState);
        Assert.NotEqual(originalDispatchId, retried.DispatchId);
        Assert.Equal(0, dispatcher.EnqueueCount);
        Assert.Equal(1, await Outbox(store, dispatcher, options, TimeProvider.System).DispatchPendingAsync(20));
        Assert.Equal(1, dispatcher.EnqueueCount);
        Assert.Equal(1, dispatcher.MessageCount);
    }

    [Fact]
    public async Task TerminalFailure_DoesNotOverwriteCompletedJob()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, Options(), TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(["ok@example.com"]));
        var processor = Processor(store, new TrackingValidator(), Options());
        await processor.ProcessAsync(job.JobId);

        var changed = await store.TrySetFailedAsync(job.JobId, "late broker failure");

        Assert.False(changed);
        Assert.Equal(ValidationJobState.Completed, (await store.GetAsync(job.JobId))!.State);
    }

    [Fact]
    public async Task TerminalFailure_MarksQueuedJobFailed()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, Options(), TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(["ok@example.com"]));

        var changed = await store.TrySetFailedAsync(job.JobId, "repeated worker failures");

        var failed = await store.GetAsync(job.JobId);
        Assert.True(changed);
        Assert.Equal(ValidationJobState.Failed, failed!.State);
        Assert.Equal("repeated worker failures", failed.FailureReason);
    }

    [Fact]
    public async Task RetryProjection_UpdatesEveryMatchingRowAndFinalCountersIdempotently()
    {
        const string validationId = "validation-shared";
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, Options(), TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(
            ["duplicate@example.com", "duplicate@example.com"]));
        var provisional = Result("duplicate@example.com", validationId,
            EmailValidationStatus.Unknown, ValidationResultState.Provisional, 1);
        var claims = await store.ClaimPendingAsync(job.JobId, 2, "worker-1", TimeSpan.FromMinutes(5));
        Assert.Equal(2, claims.Count);
        await store.CompleteClaimAsync(job.JobId, 0, "worker-1", provisional, null);
        await store.CompleteClaimAsync(job.JobId, 1, "worker-1", provisional, null);
        var final = Result("duplicate@example.com", validationId,
            EmailValidationStatus.Valid, ValidationResultState.Final, 2);
        var lifecycles = new InMemoryValidationLifecycleStore();
        await lifecycles.TrySaveAsync(new ValidationLifecycle
        {
            ValidationId = validationId,
            NormalizedEmail = "duplicate@example.com",
            Request = new EmailValidationRequest(true, JobId: job.JobId),
            ResultState = ValidationResultState.Final,
            AttemptNumber = 2,
            MaximumAttempts = 2,
            CurrentResult = final,
            Version = 1
        }, 0);
        var projector = new ValidationJobResultProjector(lifecycles, store);

        await projector.ProjectAsync(validationId);
        await projector.ProjectAsync(validationId);

        var updated = await service.GetAsync(job.JobId);
        var results = await service.GetResultsAsync(job.JobId, 0, 10);
        Assert.Equal(2, updated!.ProcessedItems);
        Assert.Equal(2, updated.FinalItems);
        Assert.Equal(0, updated.ProvisionalItems);
        Assert.All(results, item =>
        {
            Assert.Equal(EmailValidationStatus.Valid, item.Result!.Status);
            Assert.Equal(ValidationResultState.Final, item.Result.ResultState);
            Assert.Equal(2, item.Result.AttemptNumber);
        });
    }

    [Fact]
    public async Task Create_EnqueuesOneDurableMessagePerChunk()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var dispatcher = new RecordingDispatcher();
        var options = Options();
        var service = new ValidationJobService(store, options, TimeProvider.System);

        await service.CreateAsync(new CreateValidationJobRequest(
            Enumerable.Range(0, 7).Select(index => $"person{index}@example.com").ToArray()));

        Assert.Equal(0, dispatcher.EnqueueCount);
        Assert.Equal(1, await Outbox(store, dispatcher, options, TimeProvider.System).DispatchPendingAsync(20));
        Assert.Equal(1, dispatcher.EnqueueCount);
        Assert.Equal(3, dispatcher.MessageCount);
    }

    [Fact]
    public async Task Outbox_RetriesFailedPublicationWithoutLosingTheJob()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryValidationJobStore(clock);
        var options = Options();
        var service = new ValidationJobService(store, options, clock);
        var dispatcher = new RecordingDispatcher(failures: 1);
        var outbox = Outbox(store, dispatcher, options, clock);
        var job = await service.CreateAsync(new CreateValidationJobRequest(["one@example.com"]));

        Assert.Equal(0, await outbox.DispatchPendingAsync(20));
        Assert.Equal(1, dispatcher.EnqueueCount);
        Assert.Equal(ValidationJobState.Requested, (await service.GetAsync(job.JobId))!.State);
        Assert.Equal(0, await outbox.DispatchPendingAsync(20));
        Assert.Equal(1, dispatcher.EnqueueCount);

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(1, await outbox.DispatchPendingAsync(20));

        var published = await service.GetAsync(job.JobId);
        Assert.Equal(2, dispatcher.EnqueueCount);
        Assert.Equal(1, dispatcher.MessageCount);
        Assert.Equal(ValidationJobState.Queued, published!.State);
        Assert.Equal(ValidationJobDispatchState.Published, published.DispatchState);
    }

    [Fact]
    public async Task Claims_AreExclusiveAndCanBeReclaimedAfterLeaseExpiry()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryValidationJobStore(clock);
        var service = new ValidationJobService(store, Options(), clock);
        var job = await service.CreateAsync(new CreateValidationJobRequest(
            ["one@example.com", "two@example.com", "three@example.com"]));

        var first = await store.ClaimPendingAsync(job.JobId, 2, "worker-a", TimeSpan.FromMinutes(5));
        var second = await store.ClaimPendingAsync(job.JobId, 2, "worker-b", TimeSpan.FromMinutes(5));

        Assert.Equal([0, 1], first.Select(item => item.Position));
        Assert.Equal([2], second.Select(item => item.Position));
        Assert.False(await store.CompleteClaimAsync(job.JobId, 0, "worker-b", Result(
            "one@example.com", "validation-wrong", EmailValidationStatus.Valid, ValidationResultState.Final, 1), null));

        clock.Advance(TimeSpan.FromMinutes(6));
        var reclaimed = await store.ClaimPendingAsync(job.JobId, 2, "worker-c", TimeSpan.FromMinutes(5));

        Assert.Equal([0, 1], reclaimed.Select(item => item.Position));
        Assert.False(await store.CompleteClaimAsync(job.JobId, 0, "worker-a", Result(
            "one@example.com", "validation-stale", EmailValidationStatus.Valid, ValidationResultState.Final, 1), null));
        Assert.True(await store.CompleteClaimAsync(job.JobId, 0, "worker-c", Result(
            "one@example.com", "validation-current", EmailValidationStatus.Valid, ValidationResultState.Final, 1), null));
    }

    [Fact]
    public async Task Processor_SchedulesAReadyDomainBeforeContinuingAHotDomain()
    {
        var settings = Options(maximumConcurrency: 2);
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, settings, TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(
            ["first@hot.test", "second@hot.test", "only@ready.test"]));
        var validator = new TrackingValidator();

        await Processor(store, validator, settings).ProcessAsync(job.JobId);

        Assert.True(validator.StartOrder.IndexOf("only@ready.test") <
            validator.StartOrder.IndexOf("second@hot.test"));
    }

    [Fact]
    public async Task Processor_PropagatesPersistedTenantContextToEveryValidation()
    {
        var settings = Options();
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, settings, TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(
            ["one@example.com", "two@example.com"], TenantId: " tenant-a "));
        var validator = new TrackingValidator();

        await Processor(store, validator, settings).ProcessAsync(job.JobId);

        Assert.Equal("tenant-a", job.TenantId);
        Assert.Equal(2, validator.Requests.Count);
        Assert.All(validator.Requests, request =>
        {
            Assert.Equal("tenant-a", request.TenantId);
            Assert.Equal(job.JobId, request.JobId);
        });
    }

    [Fact]
    public async Task SourceFileIdentityAndLookup_AreScopedByTenant()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, Options(), TimeProvider.System);
        var first = await service.CreateAsync(new CreateValidationJobRequest(
            ["one@example.com"], SourceFileId: "shared-file", TenantId: "tenant-a"));
        var second = await service.CreateAsync(new CreateValidationJobRequest(
            ["two@example.com"], SourceFileId: "shared-file", TenantId: "tenant-b"));

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(first.JobId, (await service.GetBySourceFileIdAsync("shared-file", "tenant-a"))!.JobId);
        Assert.Equal(second.JobId, (await service.GetBySourceFileIdAsync("shared-file", "tenant-b"))!.JobId);
    }

    private static IOptions<EmailValidationOptions> Options(int maximumConcurrency = 2) =>
        Microsoft.Extensions.Options.Options.Create(new EmailValidationOptions
        {
            Jobs = new ValidationJobsOptions
            {
                MaximumItemsPerJob = 100,
                ChunkSize = 3,
                MaximumConcurrency = maximumConcurrency,
                MaximumResultPageSize = 100
            },
            Scheduling = new SchedulingOptions
            {
                GlobalConcurrency = maximumConcurrency,
                PerDomainConcurrency = 1,
                MaxActiveDomains = 100
            }
        });

    private static ValidationJobProcessor Processor(
        IValidationJobStore store,
        IEmailValidator validator,
        IOptions<EmailValidationOptions> options)
    {
        var scheduler = new DomainValidationScheduler(
            validator, new EmailNormalizer(), options, NullLogger<DomainValidationScheduler>.Instance);
        return new(store, scheduler, options, TimeProvider.System,
            NullLogger<ValidationJobProcessor>.Instance);
    }

    private static ValidationJobOutboxDispatcher Outbox(
        IValidationJobDispatchOutbox outbox,
        IValidationJobDispatcher dispatcher,
        IOptions<EmailValidationOptions> options,
        TimeProvider timeProvider) => new(
            outbox,
            dispatcher,
            options,
            timeProvider,
            NullLogger<ValidationJobOutboxDispatcher>.Instance);

    private static EmailValidationResult Result(
        string email,
        string validationId,
        EmailValidationStatus status,
        ValidationResultState state,
        int attempt) => new()
        {
            Email = email,
            NormalizedEmail = email,
            Status = status,
            Confidence = status == EmailValidationStatus.Valid ? 0.98 : 0.72,
            Checks = new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true },
            ValidationId = validationId,
            ResultState = state,
            AttemptNumber = attempt,
            MaximumAttempts = 2,
            RetryScheduled = state == ValidationResultState.Provisional
        };

    private sealed class RecordingDispatcher(int failures = 0) : IValidationJobDispatcher
    {
        private int _remainingFailures = failures;
        public string? JobId { get; private set; }
        public string? DispatchId { get; private set; }
        public int EnqueueCount { get; private set; }
        public int MessageCount { get; private set; }
        public Task EnqueueAsync(
            string jobId,
            string dispatchId,
            int chunkCount,
            CancellationToken cancellationToken = default)
        {
            JobId = jobId;
            DispatchId = dispatchId;
            EnqueueCount++;
            if (_remainingFailures-- > 0) throw new InvalidOperationException("simulated broker failure");
            MessageCount += chunkCount;
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class TrackingValidator(string? failureEmail = null) : IEmailValidator
    {
        private readonly object _sync = new();
        private int _active;
        private int _maximumActive;
        public int MaximumActive => _maximumActive;
        public List<string> StartOrder { get; } = [];
        public List<EmailValidationRequest> Requests { get; } = [];

        public async Task<EmailValidationResult> ValidateAsync(
            string email, EmailValidationRequest request, CancellationToken cancellationToken = default)
        {
            if (email == failureEmail) throw new InvalidOperationException("simulated item failure");
            lock (_sync)
            {
                StartOrder.Add(email);
                Requests.Add(request);
            }
            var active = Interlocked.Increment(ref _active);
            int observed;
            while (active > (observed = _maximumActive))
                Interlocked.CompareExchange(ref _maximumActive, active, observed);
            try { await Task.Delay(10, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
            return new EmailValidationResult
            {
                Email = email,
                NormalizedEmail = email,
                Status = EmailValidationStatus.Valid,
                Confidence = 1,
                Checks = new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true },
                ResultState = ValidationResultState.Final
            };
        }
    }
}
