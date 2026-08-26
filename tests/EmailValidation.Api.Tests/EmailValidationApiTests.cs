using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using EmailValidation.Api;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Api.Tests;

public sealed class EmailValidationApiTests : IClassFixture<EmailValidationApiFactory>
{
    private static readonly string[] DifferentEmail = ["different@example.com"];
    private static readonly string[] OneEmail = ["one@example.com"];
    private readonly EmailValidationApiFactory _factory;

    public EmailValidationApiTests(EmailValidationApiFactory factory) => _factory = factory;

    [Fact]
    public async Task BusinessEndpoint_WithoutToken_ReturnsProblemDetails401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/email-validations", new { email = "valid@example.com" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("expired")]
    public async Task InvalidOrExpiredToken_Returns401(string authenticationState)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Auth", authenticationState);
        var response = await client.GetAsync("/v1/email-validations/known");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingScope_Returns403()
    {
        using var client = _factory.CreateAuthenticatedClient([]);
        var response = await client.PostAsJsonAsync("/v1/email-validations", new { email = "valid@example.com" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Validate_ReturnsFinalVersionedCanonicalContract()
    {
        using var client = _factory.CreateAuthenticatedClient([EmailValidationScopes.Validate]);
        var response = await client.PostAsJsonAsync("/v1/email-validations", new { email = "valid@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<EmailValidationV1Response>();
        Assert.Equal("validation-final", result!.ValidationId);
        Assert.Equal("Final", result.ResultState);
        Assert.Equal("Final", result.LifecycleState);
        Assert.Equal("Valid", result.Status);
    }

    [Fact]
    public async Task Validate_ReturnsProvisionalRetryLifecycle()
    {
        using var client = _factory.CreateAuthenticatedClient([EmailValidationScopes.Validate]);
        var response = await client.PostAsJsonAsync("/v1/email-validations", new { email = "provisional@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<EmailValidationV1Response>();
        Assert.Equal("Provisional", result!.ResultState);
        Assert.Equal("RetryScheduled", result.LifecycleState);
        Assert.True(result.RetryScheduled);
        Assert.Equal("TemporarySmtpFailure", result.UnknownContext?.Cause);
        Assert.True(result.UnknownContext!.Retryable);
        Assert.Contains("Retry", result.UnknownContext.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContractMapper_BackfillsUnknownContextForLegacyStoredResults()
    {
        var result = new EmailValidationResult
        {
            Email = "legacy@example.com",
            Status = EmailValidationStatus.Unknown,
            Confidence = .72,
            Checks = new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true },
            ValidationId = "validation-legacy",
            ResultState = ValidationResultState.Final,
            ReasonCodes = [ReasonCode.ProviderVerificationBlocked],
            Diagnostics = new ValidationDiagnostics
            {
                SmtpResponseCategory = SmtpResponseCategory.VerificationBlocked
            }
        };

        var response = ApiContractMapper.Map(result);

        Assert.Equal("ProviderVerificationBlocked", response.UnknownContext?.Cause);
        Assert.True(response.UnknownContext!.Retryable);
        Assert.Contains("provider cooldown", response.UnknownContext.RecommendedAction,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidShape_ReturnsTraceableProblemDetails()
    {
        using var client = _factory.CreateAuthenticatedClient([EmailValidationScopes.Validate]);
        var response = await client.PostAsJsonAsync("/v1/email-validations", new { email = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Status_RequiresReadScopeAndEnforcesTenantOwnership()
    {
        using var owner = _factory.CreateAuthenticatedClient([EmailValidationScopes.Read], tenant: "tenant-a");
        using var other = _factory.CreateAuthenticatedClient([EmailValidationScopes.Read], tenant: "tenant-b");
        using var validateOnly = _factory.CreateAuthenticatedClient([EmailValidationScopes.Validate], tenant: "tenant-a");

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/v1/email-validations/known")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/v1/email-validations/known")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await validateOnly.GetAsync("/v1/email-validations/known")).StatusCode);
    }

    [Fact]
    public async Task Cancellation_CancelsOnlyTheRequestWaiter()
    {
        using var client = _factory.CreateAuthenticatedClient([EmailValidationScopes.Validate]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostAsJsonAsync(
            "/v1/email-validations", new { email = "wait@example.com" }, cancellation.Token));
        await _factory.Validator.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Jobs_AreScopedPagedAndIdempotent()
    {
        using var writer = _factory.CreateAuthenticatedClient(
            [EmailValidationScopes.JobsWrite, EmailValidationScopes.JobsRead], tenant: "tenant-a");
        writer.DefaultRequestHeaders.Add("Idempotency-Key", "job-request-1");
        var request = new
        {
            emails = new[] { "one@example.com", "two@example.com" },
            sourceFileId = "search-42",
            sourceFileName = "customers.csv",
            emailColumn = "Business Email"
        };

        var first = await writer.PostAsJsonAsync("/v1/email-validation-jobs", request);
        var second = await writer.PostAsJsonAsync("/v1/email-validation-jobs", request);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstJob = await first.Content.ReadFromJsonAsync<ValidationJobV1Response>();
        var secondJob = await second.Content.ReadFromJsonAsync<ValidationJobV1Response>();
        Assert.Equal(firstJob!.JobId, secondJob!.JobId);
        Assert.Equal("search-42", firstJob.SourceFileId);
        Assert.Equal("customers.csv", firstJob.SourceFileName);

        var history = await writer.GetFromJsonAsync<ValidationJobPageV1Response>(
            "/v1/email-validation-jobs?skip=0&take=25");
        var historyItem = Assert.Single(history!.Items);
        Assert.Equal(firstJob.JobId, historyItem.JobId);
        Assert.Equal("Business Email", historyItem.EmailColumn);

        var page = await writer.GetFromJsonAsync<ValidationJobResultsPageV1Response>(
            $"/v1/email-validation-jobs/{firstJob.JobId}/results?skip=0&take=1");
        Assert.Single(page!.Items);
        Assert.Equal(1, page.NextSkip);

        using var otherTenant = _factory.CreateAuthenticatedClient([EmailValidationScopes.JobsRead], tenant: "tenant-b");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await otherTenant.GetAsync($"/v1/email-validation-jobs/{firstJob.JobId}")).StatusCode);
        var otherHistory = await otherTenant.GetFromJsonAsync<ValidationJobPageV1Response>(
            "/v1/email-validation-jobs");
        Assert.Empty(otherHistory!.Items);

        var conflict = await writer.PostAsJsonAsync("/v1/email-validation-jobs",
            new { emails = DifferentEmail });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task CompletedSourceFile_CannotBeValidatedAgain()
    {
        var sourceFileId = $"completed-source-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(
            [EmailValidationScopes.JobsWrite, EmailValidationScopes.JobsRead]);
        var request = new { emails = OneEmail, sourceFileId, sourceFileName = "completed.csv" };
        var first = await client.PostAsJsonAsync("/v1/email-validation-jobs", request);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        _factory.JobService.CompleteSourceFile(sourceFileId);

        var duplicate = await client.PostAsJsonAsync("/v1/email-validation-jobs", request);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var problem = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal("File already validated", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ReadScope_CannotCreateJob()
    {
        using var client = _factory.CreateAuthenticatedClient([EmailValidationScopes.JobsRead]);
        var response = await client.PostAsJsonAsync("/v1/email-validation-jobs",
            new { emails = OneEmail });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("search:execute")]
    [InlineData("match:execute")]
    [InlineData("openmeta.write")]
    public async Task ExistingOpenMetaWritePermission_CanCreateJob(string permission)
    {
        using var client = _factory.CreateAuthenticatedClient([], permissions: [permission]);
        var response = await client.PostAsJsonAsync("/v1/email-validation-jobs",
            new { emails = OneEmail });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Health_IsAnonymousAndMinimal()
    {
        using var client = _factory.CreateClient();
        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await live.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cors_AllowsOnlyTheProductionBrowserOrigin()
    {
        using var client = _factory.CreateClient();
        using var allowedRequest = CreatePreflightRequest("https://app.digitalwarehouse.io");
        using var allowed = await client.SendAsync(allowedRequest);

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.True(allowed.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowedOrigins),
            $"The allowed preflight omitted Access-Control-Allow-Origin. Headers: {allowed.Headers}");
        Assert.Equal("https://app.digitalwarehouse.io", Assert.Single(allowedOrigins));
        Assert.True(allowed.Headers.TryGetValues("Access-Control-Allow-Headers", out var allowedHeaders));
        Assert.Contains("X-Correlation-ID", Assert.Single(allowedHeaders), StringComparison.OrdinalIgnoreCase);

        using var deniedRequest = CreatePreflightRequest("https://unapproved.example");
        using var denied = await client.SendAsync(deniedRequest);
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Swagger_IsRestrictedOutsideDevelopmentAndContractHasScopes()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/swagger/index.html")).StatusCode);

        using var admin = _factory.CreateAuthenticatedClient([EmailValidationScopes.Admin]);
        var response = await admin.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/v1/email-validations", out var path));
        Assert.Equal("CreateEmailValidationV1", path.GetProperty("post").GetProperty("operationId").GetString());
        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("oauth2", out _));
        Assert.Equal(EmailValidationScopes.Validate,
            path.GetProperty("post").GetProperty("security")[0].GetProperty("oauth2")[0].GetString());
    }

    private static HttpRequestMessage CreatePreflightRequest(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/v1/email-validations");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add(
            "Access-Control-Request-Headers",
            "authorization,content-type,idempotency-key,x-correlation-id");
        return request;
    }
}

public sealed class EmailValidationApiFactory : WebApplicationFactory<Program>
{
    public ApiValidator Validator { get; } = new();
    public InMemoryCommercialResourceStore Resources { get; } = new();
    private readonly ApiJobService _jobs = new();
    public ApiJobService JobService => _jobs;

    public EmailValidationApiFactory()
    {
        Resources.GrantAsync(new ResourceOwnership(
            OwnedResourceType.Validation,
            "known",
            "tenant:tenant-a:subject:consumer-a",
            "consumer-a",
            "tenant-a",
            DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
        Resources.GrantAsync(new ResourceOwnership(
            OwnedResourceType.Validation,
            "validation-provisional",
            "tenant:tenant-a:subject:consumer-a",
            "consumer-a",
            "tenant-a",
            DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
    }

    public HttpClient CreateAuthenticatedClient(
        IReadOnlyList<string> scopes,
        string tenant = "tenant-a",
        IReadOnlyList<string>? permissions = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Auth", "valid");
        client.DefaultRequestHeaders.Add("X-Test-Subject", "consumer-a");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Test-Scopes", string.Join(' ', scopes));
        if (permissions is { Count: > 0 })
            client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Api:OpenApi:ExposeInProduction"] = "true",
                ["Api:Cors:AllowedOrigins:0"] = "https://app.digitalwarehouse.io",
                ["EmailValidation:Persistence:Enabled"] = "false",
                ["EmailValidation:Persistence:Provider"] = "Json",
                ["EmailValidation:Persistence:StoragePath"] = "test-data",
                ["EmailValidation:ProbeSenderSource:Index"] = "test",
                ["EmailValidation:ProbeSenderSource:Query:match_all:enabled"] = "true"
            }));
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(TestAuthenticationHandler.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.AuthenticationScheme, _ => { });
            services.RemoveAll<IEmailValidator>();
            services.RemoveAll<IValidationStatusQueryService>();
            services.RemoveAll<IValidationJobService>();
            services.RemoveAll<ICommercialResourceStore>();
            services.AddSingleton<IEmailValidator>(Validator);
            services.AddSingleton<IValidationStatusQueryService, ApiStatusService>();
            services.AddSingleton<IValidationJobService>(_jobs);
            services.AddSingleton<ICommercialResourceStore>(Resources);
        });
    }
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationScheme = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var state = Request.Headers["X-Test-Auth"].ToString();
        if (string.IsNullOrWhiteSpace(state)) return Task.FromResult(AuthenticateResult.NoResult());
        if (state is "invalid" or "expired")
            return Task.FromResult(AuthenticateResult.Fail("The test access token is invalid."));
        var claims = new List<Claim>
        {
            new Claim("sub", Request.Headers["X-Test-Subject"].FirstOrDefault() ?? "consumer-a"),
            new Claim("tenant_id", Request.Headers["X-Test-Tenant"].FirstOrDefault() ?? "tenant-a"),
            new Claim("scope", Request.Headers["X-Test-Scopes"].FirstOrDefault() ?? string.Empty)
        };
        claims.AddRange(Request.Headers["X-Test-Permissions"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(permission => new Claim("permissions", permission)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationScheme)), AuthenticationScheme)));
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
        var now = DateTimeOffset.UtcNow;
        return new EmailValidationResult
        {
            Email = email,
            NormalizedEmail = email,
            Status = provisional ? EmailValidationStatus.Unknown : EmailValidationStatus.Valid,
            SubStatus = provisional ? DetailedStatus.TemporaryFailure : DetailedStatus.MailboxAccepted,
            Confidence = provisional ? .8 : 1,
            Checks = new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true },
            ValidationId = request.ValidationId ?? (provisional ? "validation-provisional" : "validation-final"),
            ResultState = provisional ? ValidationResultState.Provisional : ValidationResultState.Final,
            RetryScheduled = provisional,
            RetryAfter = provisional ? now.AddMinutes(15) : null,
            UnknownContext = provisional
                ? new(
                    UnknownCause.TemporarySmtpFailure,
                    "The destination returned a temporary SMTP failure.",
                    true,
                    "Retry after the destination or provider cooldown clears.",
                    SmtpResponseCategory.TemporaryFailure,
                    SmtpCommand.RcptTo,
                    451,
                    "4.7.1",
                    "mx.example.com",
                    now.AddMinutes(15))
                : null,
            FirstValidatedAt = now,
            LastValidatedAt = now,
            FinalizedAt = provisional ? null : now
        };
    }
}

public sealed class ApiStatusService : IValidationStatusQueryService
{
    public Task<ValidationStatusSnapshot?> GetAsync(string validationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ValidationStatusSnapshot?>(validationId is "known" or "validation-final" or "validation-provisional"
            ? new ValidationStatusSnapshot
            {
                ValidationId = validationId,
                Email = "valid@example.com",
                LifecycleState = validationId == "validation-provisional"
                    ? ValidationLifecycleState.RetryWaiting
                    : ValidationLifecycleState.Final,
                ResultState = validationId == "validation-provisional"
                    ? ValidationResultState.Provisional
                    : ValidationResultState.Final,
                Status = validationId == "validation-provisional"
                    ? EmailValidationStatus.Unknown
                    : EmailValidationStatus.Valid,
                RetryScheduled = validationId == "validation-provisional",
                RetryAt = validationId == "validation-provisional" ? DateTimeOffset.UtcNow.AddMinutes(15) : null,
                UnknownContext = validationId == "validation-provisional"
                    ? new(
                        UnknownCause.TemporarySmtpFailure,
                        "The destination returned a temporary SMTP failure.",
                        true,
                        "Retry after the destination or provider cooldown clears.",
                        SmtpResponseCategory.TemporaryFailure)
                    : null,
                Sequence = 3,
                LastUpdatedAt = DateTimeOffset.UtcNow
            }
            : null);
}

public sealed class ApiJobService : IValidationJobService
{
    private readonly Dictionary<string, (ValidationJobSnapshot Job, ValidationJobItem[] Items)> _jobs = [];

    public Task<ValidationJobSnapshot> CreateAsync(CreateValidationJobRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = request.JobId ?? Guid.NewGuid().ToString("N");
        var job = new ValidationJobSnapshot(
            id,
            now,
            ValidationJobState.Queued,
            request.Emails.Count,
            0,
            0,
            0,
            0,
            now,
            SourceFileId: request.SourceFileId,
            SourceFileName: request.SourceFileName,
            EmailColumn: request.EmailColumn);
        _jobs[id] = (job, request.Emails.Select((email, position) =>
            new ValidationJobItem(id, position, email, ValidationJobItemState.Pending)).ToArray());
        return Task.FromResult(job);
    }

    public Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ValidationJobSnapshot?>(_jobs.TryGetValue(jobId, out var value) ? value.Job : null);

    public Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(
        string sourceFileId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ValidationJobSnapshot?>(_jobs.Values
            .Select(value => value.Job)
            .OrderByDescending(job => job.CreatedAtUtc)
            .FirstOrDefault(job => string.Equals(job.SourceFileId, sourceFileId, StringComparison.Ordinal)));

    public void CompleteSourceFile(string sourceFileId)
    {
        var pair = _jobs.First(entry => string.Equals(
            entry.Value.Job.SourceFileId, sourceFileId, StringComparison.Ordinal));
        _jobs[pair.Key] = (pair.Value.Job with
        {
            State = ValidationJobState.Completed,
            ProcessedItems = pair.Value.Job.TotalItems,
            FinalItems = pair.Value.Job.TotalItems,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, pair.Value.Items);
    }

    public Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(
        string jobId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ValidationJobItem>>(
            _jobs[jobId].Items.Skip(skip).Take(take).ToArray());
}
