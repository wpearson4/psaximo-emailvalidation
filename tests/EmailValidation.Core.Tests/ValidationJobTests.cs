using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class ValidationJobTests
{
    [Fact]
    public async Task Create_PersistsItemsAndQueuesIdentifierOnly()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var dispatcher = new RecordingDispatcher();
        var service = new ValidationJobService(store, dispatcher, Options(), TimeProvider.System);

        var job = await service.CreateAsync(new CreateValidationJobRequest(
            ["one@example.com", "two@example.com"], EnableSmtp: false));

        Assert.Equal(ValidationJobState.Queued, job.State);
        Assert.Equal(2, job.TotalItems);
        Assert.False(job.EnableSmtp);
        Assert.Equal(job.JobId, dispatcher.JobId);
        Assert.Equal(["one@example.com", "two@example.com"],
            (await service.GetResultsAsync(job.JobId, 0, 10)).Select(value => value.Email));
    }

    [Fact]
    public async Task Processor_UsesBoundedConcurrencyAndCompletesWithProgress()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var dispatcher = new RecordingDispatcher();
        var service = new ValidationJobService(store, dispatcher, Options(maximumConcurrency: 2), TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(
            Enumerable.Range(0, 7).Select(index => $"person{index}@example.com").ToArray()));
        var validator = new TrackingValidator();
        var processor = new ValidationJobProcessor(store, validator, Options(maximumConcurrency: 2),
            NullLogger<ValidationJobProcessor>.Instance);

        await processor.ProcessAsync(job.JobId);

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
        var service = new ValidationJobService(store, new RecordingDispatcher(), Options(), TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(["ok@example.com", "fail@example.com"]));
        var processor = new ValidationJobProcessor(store, new TrackingValidator("fail@example.com"), Options(),
            NullLogger<ValidationJobProcessor>.Instance);

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
        var service = new ValidationJobService(store, new RecordingDispatcher(), Options(), TimeProvider.System);
        var request = new CreateValidationJobRequest(
            ["ok@example.com"], SourceFileId: "source-file-1", SourceFileName: "source.csv");
        var job = await service.CreateAsync(request);
        var processor = new ValidationJobProcessor(store, new TrackingValidator(), Options(),
            NullLogger<ValidationJobProcessor>.Instance);
        await processor.ProcessAsync(job.JobId);

        await Assert.ThrowsAsync<ValidationJobSourceFileCompletedException>(() => service.CreateAsync(request));
    }

    [Fact]
    public async Task Create_RequeuesFailedSourceFileWithoutDuplicatingIt()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var dispatcher = new RecordingDispatcher();
        var service = new ValidationJobService(store, dispatcher, Options(), TimeProvider.System);
        var request = new CreateValidationJobRequest(
            ["ok@example.com"], SourceFileId: "source-file-1", SourceFileName: "source.csv");
        var job = await service.CreateAsync(request);
        await store.TrySetFailedAsync(job.JobId, "worker failure");

        var retried = await service.CreateAsync(request);

        Assert.Equal(job.JobId, retried.JobId);
        Assert.Equal(ValidationJobState.Queued, retried.State);
        Assert.Equal(2, dispatcher.EnqueueCount);
    }

    [Fact]
    public async Task TerminalFailure_DoesNotOverwriteCompletedJob()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, new RecordingDispatcher(), Options(), TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(["ok@example.com"]));
        var processor = new ValidationJobProcessor(store, new TrackingValidator(), Options(),
            NullLogger<ValidationJobProcessor>.Instance);
        await processor.ProcessAsync(job.JobId);

        var changed = await store.TrySetFailedAsync(job.JobId, "late broker failure");

        Assert.False(changed);
        Assert.Equal(ValidationJobState.Completed, (await store.GetAsync(job.JobId))!.State);
    }

    [Fact]
    public async Task TerminalFailure_MarksQueuedJobFailed()
    {
        var store = new InMemoryValidationJobStore(TimeProvider.System);
        var service = new ValidationJobService(store, new RecordingDispatcher(), Options(), TimeProvider.System);
        var job = await service.CreateAsync(new CreateValidationJobRequest(["ok@example.com"]));

        var changed = await store.TrySetFailedAsync(job.JobId, "repeated worker failures");

        var failed = await store.GetAsync(job.JobId);
        Assert.True(changed);
        Assert.Equal(ValidationJobState.Failed, failed!.State);
        Assert.Equal("repeated worker failures", failed.FailureReason);
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
            }
        });

    private sealed class RecordingDispatcher : IValidationJobDispatcher
    {
        public string? JobId { get; private set; }
        public int EnqueueCount { get; private set; }
        public Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default)
        {
            JobId = jobId;
            EnqueueCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingValidator(string? failureEmail = null) : IEmailValidator
    {
        private int _active;
        private int _maximumActive;
        public int MaximumActive => _maximumActive;

        public async Task<EmailValidationResult> ValidateAsync(
            string email, EmailValidationRequest request, CancellationToken cancellationToken = default)
        {
            if (email == failureEmail) throw new InvalidOperationException("simulated item failure");
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
