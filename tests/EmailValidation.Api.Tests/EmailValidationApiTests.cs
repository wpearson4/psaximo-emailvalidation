using System.Net;
using System.Net.Http.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmailValidation.Api.Tests;

public sealed class EmailValidationApiTests : IClassFixture<EmailValidationApiFactory>
{
    private readonly EmailValidationApiFactory _factory;
    private readonly HttpClient _client;

    public EmailValidationApiTests(EmailValidationApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Validate_ReturnsFinalCanonicalResult()
    {
        var response = await _client.PostAsJsonAsync("/v1/email/validate", new { email = "valid@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<EmailValidationResult>();
        Assert.Equal("validation-final", result!.ValidationId);
        Assert.Equal(ValidationResultState.Final, result.ResultState);
    }

    [Fact]
    public async Task Validate_ReturnsProvisionalWithoutHoldingRequestForRetry()
    {
        var response = await _client.PostAsJsonAsync("/v1/email/validate", new { email = "provisional@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<EmailValidationResult>();
        Assert.Equal(ValidationResultState.Provisional, result!.ResultState);
        Assert.True(result.RetryScheduled);
    }

    [Fact]
    public async Task Validate_RejectsMissingEmail()
    {
        var response = await _client.PostAsJsonAsync("/v1/email/validate", new { email = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Status_ReturnsCanonicalSnapshotOrNotFound()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/v1/email-validations/known")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/v1/email-validations/missing")).StatusCode);
    }

    [Fact]
    public async Task HttpCancellation_CancelsWaiterToken()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.PostAsJsonAsync(
            "/v1/email/validate", new { email = "wait@example.com" }, cancellation.Token));

        await _factory.Validator.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task JobEndpoints_CreateQueryAndReturnOrderedResults()
    {
        var created = await _client.PostAsJsonAsync("/v1/email-validation/jobs",
            new { emails = new List<string> { "one@example.com", "two@example.com" } });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = await created.Content.ReadFromJsonAsync<ValidationJobSnapshot>();

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/v1/email-validation/jobs/{job!.JobId}")).StatusCode);
        var results = await _client.GetFromJsonAsync<ValidationJobItem[]>(
            $"/v1/email-validation/jobs/{job.JobId}/results");
        Assert.Collection(results!,
            item => Assert.Equal("one@example.com", item.Email),
            item => Assert.Equal("two@example.com", item.Email));
    }
}

public sealed class EmailValidationApiFactory : WebApplicationFactory<Program>
{
    public ApiValidator Validator { get; } = new();
    private readonly ApiJobService _jobs = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["EmailValidation:Persistence:Enabled"] = "false",
                ["EmailValidation:Persistence:Provider"] = "Json",
                ["EmailValidation:Persistence:StoragePath"] = "test-data",
                ["EmailValidation:ProbeSenderSource:Index"] = "test",
                ["EmailValidation:ProbeSenderSource:Query:match_all:enabled"] = "true"
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailValidator>();
            services.RemoveAll<IValidationStatusQueryService>();
            services.RemoveAll<IValidationJobService>();
            services.AddSingleton<IEmailValidator>(Validator);
            services.AddSingleton<IValidationStatusQueryService, ApiStatusService>();
            services.AddSingleton<IValidationJobService>(_jobs);
        });
    }
}

public sealed class ApiValidator : IEmailValidator
{
    public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<EmailValidationResult> ValidateAsync(
        string email, EmailValidationRequest request, CancellationToken cancellationToken = default)
    {
        if (email == "wait@example.com")
        {
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        var provisional = email == "provisional@example.com";
        return new EmailValidationResult
        {
            Email = email,
            NormalizedEmail = email,
            Status = provisional ? EmailValidationStatus.Unknown : EmailValidationStatus.Valid,
            Confidence = provisional ? .8 : 1,
            Checks = new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true },
            ValidationId = provisional ? "validation-provisional" : "validation-final",
            ResultState = provisional ? ValidationResultState.Provisional : ValidationResultState.Final,
            RetryScheduled = provisional
        };
    }
}

public sealed class ApiStatusService : IValidationStatusQueryService
{
    public Task<ValidationStatusSnapshot?> GetAsync(string validationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ValidationStatusSnapshot?>(validationId == "known" ? new ValidationStatusSnapshot
        {
            ValidationId = validationId,
            LifecycleState = ValidationLifecycleState.Final,
            ResultState = ValidationResultState.Final,
            Sequence = 3
        } : null);
}

public sealed class ApiJobService : IValidationJobService
{
    private readonly Dictionary<string, (ValidationJobSnapshot Job, ValidationJobItem[] Items)> _jobs = [];
    public Task<ValidationJobSnapshot> CreateAsync(CreateValidationJobRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var job = new ValidationJobSnapshot(id, now, ValidationJobState.Queued, request.Emails.Count, 0, 0, 0, 0, now);
        _jobs[id] = (job, request.Emails.Select((email, position) =>
            new ValidationJobItem(id, position, email, ValidationJobItemState.Pending)).ToArray());
        return Task.FromResult(job);
    }
    public Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ValidationJobSnapshot?>(_jobs.TryGetValue(jobId, out var value) ? value.Job : null);
    public Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(string jobId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ValidationJobItem>>(_jobs[jobId].Items.Skip(skip).Take(take).ToArray());
}
