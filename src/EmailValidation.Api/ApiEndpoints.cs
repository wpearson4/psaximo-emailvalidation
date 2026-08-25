using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EmailValidation.Api;

public static class ApiEndpoints
{
    private const string CreateJobOperation = "email-validation-jobs.create.v1";

    public static IEndpointRouteBuilder MapEmailValidationV1(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1")
            .WithTags("Email Validation v1");

        group.MapPost("/email-validations", ValidateEmailAsync)
            .WithName("CreateEmailValidationV1")
            .WithSummary("Validate one email address")
            .WithDescription("Runs the shared validation engine and returns the canonical final or provisional validation resource. Mailbox invalidity is a successful HTTP operation.")
            .RequireAuthorization(EmailValidationPolicies.Validate)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Accepts<ValidateEmailV1Request>("application/json")
            .Produces<EmailValidationV1Response>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status429TooManyRequests);

        group.MapGet("/email-validations/{validationId}", GetValidationAsync)
            .WithName("GetEmailValidationV1")
            .WithSummary("Get canonical validation status")
            .WithDescription("Reads canonical lifecycle state without starting a new SMTP validation.")
            .RequireAuthorization(EmailValidationPolicies.Read)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Produces<ValidationStatusV1Response>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapPost("/email-validation-jobs", CreateJobAsync)
            .WithName("CreateEmailValidationJobV1")
            .WithSummary("Create a durable bulk validation job")
            .WithDescription("Persists a bulk job and submits it to the existing Service Bus worker. Idempotency-Key is supported and scoped to the authenticated consumer.")
            .RequireAuthorization(EmailValidationPolicies.JobsWrite)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Accepts<CreateValidationJobV1Request>("application/json")
            .Produces<ValidationJobV1Response>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status429TooManyRequests);

        group.MapGet("/email-validation-jobs", ListJobsAsync)
            .WithName("ListEmailValidationJobsV1")
            .WithSummary("List validation job history for the authenticated consumer")
            .WithDescription("Returns owned jobs in reverse chronological order for durable cross-browser history.")
            .RequireAuthorization(EmailValidationPolicies.JobsRead)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Produces<ValidationJobPageV1Response>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        group.MapGet("/email-validation-jobs/{jobId}", GetJobAsync)
            .WithName("GetEmailValidationJobV1")
            .WithSummary("Get a durable validation job")
            .RequireAuthorization(EmailValidationPolicies.JobsRead)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Produces<ValidationJobV1Response>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapGet("/email-validation-jobs/{jobId}/results", GetJobResultsAsync)
            .WithName("GetEmailValidationJobResultsV1")
            .WithSummary("Get an ordered page of job results")
            .WithDescription("Returns a bounded result page. Use nextSkip to request the next page.")
            .RequireAuthorization(EmailValidationPolicies.JobsRead)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Produces<ValidationJobResultsPageV1Response>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ValidateEmailAsync(
        ValidateEmailV1Request? input,
        IEmailValidator validator,
        ICurrentConsumerContext consumers,
        ICommercialResourceStore resources,
        TimeProvider timeProvider,
        IOptions<ApiHostOptions> hostOptions,
        CancellationToken cancellationToken)
    {
        var limits = hostOptions.Value.Limits;
        if (input is null || string.IsNullOrWhiteSpace(input.Email))
            return ValidationError("email", "Email is required.");
        if (input.Email.Length > limits.MaximumEmailLength)
            return ValidationError("email", $"Email must not exceed {limits.MaximumEmailLength} characters.");
        if (input.ValidationId is not null && !ValidIdentifier(input.ValidationId, limits.MaximumIdentifierLength))
            return ValidationError("validationId", "ValidationId contains unsupported characters or is too long.");

        var result = await validator.ValidateAsync(input.Email,
            new EmailValidationRequest(input.EnableSmtp, input.Verbose, input.ValidationId), cancellationToken)
            .ConfigureAwait(false);
        var response = ApiContractMapper.Map(result);
        var consumer = consumers.GetRequiredConsumer();
        await resources.GrantAsync(new ResourceOwnership(
            OwnedResourceType.Validation,
            response.ValidationId,
            consumer.PrincipalKey,
            consumer.SubjectId,
            consumer.TenantId,
            timeProvider.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetValidationAsync(
        string validationId,
        ICurrentConsumerContext consumers,
        IValidationAccessPolicy accessPolicy,
        IValidationStatusQueryService statuses,
        IOptions<ApiHostOptions> hostOptions,
        CancellationToken cancellationToken)
    {
        if (!ValidIdentifier(validationId, hostOptions.Value.Limits.MaximumIdentifierLength))
            return ValidationError("validationId", "ValidationId is invalid.");
        var consumer = consumers.GetRequiredConsumer();
        var permitted = await accessPolicy.CanAccessAsync(validationId,
            new ValidationAccessContext(consumer.SubjectId, consumer.TenantId, consumer.Scopes), cancellationToken)
            .ConfigureAwait(false);
        if (!permitted) return Results.Forbid();
        var status = await statuses.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
        return status is null
            ? Problem(StatusCodes.Status404NotFound, "Validation not found", "The validation resource does not exist.")
            : Results.Ok(ApiContractMapper.Map(status));
    }

    private static async Task<IResult> CreateJobAsync(
        HttpContext http,
        CreateValidationJobV1Request? input,
        IValidationJobService jobs,
        ICurrentConsumerContext consumers,
        ICommercialResourceStore resources,
        TimeProvider timeProvider,
        IOptions<ApiHostOptions> hostOptions,
        IOptions<EmailValidationOptions> engineOptions,
        CancellationToken cancellationToken)
    {
        if (input?.Emails is null || input.Emails.Count == 0)
            return ValidationError("emails", "At least one email is required.");
        var limits = hostOptions.Value.Limits;
        if (input.Emails.Any(email => string.IsNullOrWhiteSpace(email) || email.Length > limits.MaximumEmailLength))
            return ValidationError("emails", $"Each email is required and must not exceed {limits.MaximumEmailLength} characters.");
        if (input.Emails.Count > engineOptions.Value.Jobs.MaximumItemsPerJob)
            return ValidationError("emails",
                $"A job may contain at most {engineOptions.Value.Jobs.MaximumItemsPerJob} items.");
        if (!ValidOptionalMetadata(input.SourceFileId, 256) ||
            !ValidOptionalMetadata(input.SourceFileName, 512) ||
            !ValidOptionalMetadata(input.EmailColumn, 256))
            return ValidationError("source", "Source file metadata contains unsupported characters or is too long.");
        var consumer = consumers.GetRequiredConsumer();
        var sourceFileId = input.SourceFileId?.Trim();
        ValidationJobSnapshot? sourceJob = null;
        if (!string.IsNullOrWhiteSpace(sourceFileId))
        {
            sourceJob = await jobs.GetBySourceFileIdAsync(sourceFileId, cancellationToken).ConfigureAwait(false);
            if (sourceJob?.State is ValidationJobState.Completed or ValidationJobState.CompletedWithErrors)
                return Problem(StatusCodes.Status409Conflict, "File already validated",
                    "This source file already has a completed validation job.");
        }
        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (!string.IsNullOrEmpty(key) && (key.Length > limits.MaximumIdempotencyKeyLength ||
                key.Any(character => char.IsControl(character))))
            return ValidationError("Idempotency-Key", "Idempotency-Key is invalid or too long.");

        var hash = IdempotencyRequestHasher.HashJobRequest(
            input.Emails, input.EnableSmtp, sourceFileId, input.EmailColumn);
        if (!string.IsNullOrEmpty(key))
        {
            var existing = await resources.GetIdempotentOperationAsync(
                consumer.PrincipalKey, CreateJobOperation, key, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
                    return Problem(StatusCodes.Status409Conflict, "Idempotency conflict",
                        "The Idempotency-Key was already used with a different request.");
                var existingJob = await jobs.GetAsync(existing.ResourceId, cancellationToken).ConfigureAwait(false);
                if (existingJob is null)
                    return Problem(StatusCodes.Status409Conflict, "Job creation in progress",
                        "The idempotent operation is still being created. Retry shortly.");
                if (existingJob.State == ValidationJobState.Failed && !string.IsNullOrWhiteSpace(sourceFileId))
                {
                    existingJob = await jobs.CreateAsync(new CreateValidationJobRequest(
                        input.Emails,
                        input.EnableSmtp,
                        existingJob.JobId,
                        sourceFileId,
                        input.SourceFileName?.Trim(),
                        input.EmailColumn?.Trim()), CancellationToken.None).ConfigureAwait(false);
                }
                return Results.Accepted($"/v1/email-validation-jobs/{existingJob.JobId}",
                    ApiContractMapper.Map(existingJob));
            }
        }

        var jobId = string.IsNullOrWhiteSpace(sourceFileId)
            ? Guid.NewGuid().ToString("N")
            : sourceJob?.JobId ?? ValidationJobIdentity.FromSourceFileId(sourceFileId);
        if (!string.IsNullOrEmpty(key))
        {
            var saved = await resources.TrySaveIdempotentOperationAsync(new IdempotentOperation(
                consumer.PrincipalKey, CreateJobOperation, key, hash, jobId, timeProvider.GetUtcNow()), cancellationToken)
                .ConfigureAwait(false);
            if (!saved)
                return Problem(StatusCodes.Status409Conflict, "Job creation in progress",
                    "A concurrent request is creating this idempotent operation. Retry shortly.");
        }

        try
        {
            var job = await jobs.CreateAsync(
                new CreateValidationJobRequest(
                    input.Emails,
                    input.EnableSmtp,
                    jobId,
                    sourceFileId,
                    input.SourceFileName?.Trim(),
                    input.EmailColumn?.Trim()),
                CancellationToken.None)
                .ConfigureAwait(false);
            await resources.GrantAsync(new ResourceOwnership(
                OwnedResourceType.ValidationJob,
                job.JobId,
                consumer.PrincipalKey,
                consumer.SubjectId,
                consumer.TenantId,
                timeProvider.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
            return Results.Accepted($"/v1/email-validation-jobs/{job.JobId}", ApiContractMapper.Map(job));
        }
        catch (ArgumentException exception)
        {
            return ValidationError("emails", exception.Message);
        }
        catch (ValidationJobSourceFileCompletedException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "File already validated", exception.Message);
        }
        catch (ValidationJobSourceFileActiveException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "File validation already in progress", exception.Message);
        }
    }

    private static async Task<IResult> ListJobsAsync(
        int? skip,
        int? take,
        IValidationJobService jobs,
        ICommercialResourceStore resources,
        ICurrentConsumerContext consumers,
        CancellationToken cancellationToken)
    {
        if (skip is < 0 || take is < 1)
            return ValidationError("pagination", "skip must be non-negative and take must be positive.");
        var actualSkip = skip ?? 0;
        var actualTake = Math.Clamp(take ?? 25, 1, 100);
        var consumer = consumers.GetRequiredConsumer();
        var owned = await resources.ListOwnedAsync(
            OwnedResourceType.ValidationJob,
            consumer.PrincipalKey,
            actualSkip,
            actualTake + 1,
            cancellationToken).ConfigureAwait(false);
        var pageReferences = owned.Take(actualTake).ToArray();
        var snapshots = await Task.WhenAll(pageReferences.Select(reference =>
            jobs.GetAsync(reference.ResourceId, cancellationToken))).ConfigureAwait(false);
        var items = snapshots.Where(snapshot => snapshot is not null)
            .Select(snapshot => ApiContractMapper.Map(snapshot!))
            .ToArray();
        var nextSkip = owned.Count > actualTake ? actualSkip + actualTake : (int?)null;
        return Results.Ok(new ValidationJobPageV1Response(actualSkip, actualTake, items, nextSkip));
    }

    private static async Task<IResult> GetJobAsync(
        string jobId,
        IValidationJobService jobs,
        IValidationJobAccessPolicy accessPolicy,
        ICurrentConsumerContext consumers,
        IOptions<ApiHostOptions> hostOptions,
        CancellationToken cancellationToken)
    {
        if (!ValidIdentifier(jobId, hostOptions.Value.Limits.MaximumIdentifierLength))
            return ValidationError("jobId", "JobId is invalid.");
        if (!await accessPolicy.CanAccessAsync(jobId, consumers.GetRequiredConsumer(), cancellationToken)
                .ConfigureAwait(false))
            return Results.Forbid();
        var job = await jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        return job is null
            ? Problem(StatusCodes.Status404NotFound, "Job not found", "The validation job does not exist.")
            : Results.Ok(ApiContractMapper.Map(job));
    }

    private static async Task<IResult> GetJobResultsAsync(
        string jobId,
        int? skip,
        int? take,
        IValidationJobService jobs,
        IValidationJobAccessPolicy accessPolicy,
        ICurrentConsumerContext consumers,
        IOptions<ApiHostOptions> hostOptions,
        CancellationToken cancellationToken)
    {
        var limits = hostOptions.Value.Limits;
        if (!ValidIdentifier(jobId, limits.MaximumIdentifierLength))
            return ValidationError("jobId", "JobId is invalid.");
        if (skip is < 0 || take is < 1)
            return ValidationError("pagination", "skip must be non-negative and take must be positive.");
        if (!await accessPolicy.CanAccessAsync(jobId, consumers.GetRequiredConsumer(), cancellationToken)
                .ConfigureAwait(false))
            return Results.Forbid();
        var job = await jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return Problem(StatusCodes.Status404NotFound, "Job not found", "The validation job does not exist.");
        var actualSkip = skip ?? 0;
        var actualTake = take ?? limits.DefaultJobResultPageSize;
        var items = await jobs.GetResultsAsync(jobId, actualSkip, actualTake, cancellationToken).ConfigureAwait(false);
        int? next = actualSkip + items.Count < job.TotalItems ? actualSkip + items.Count : null;
        return Results.Ok(new ValidationJobResultsPageV1Response(
            jobId, actualSkip, actualTake, items.Select(ApiContractMapper.Map).ToArray(), next));
    }

    private static IResult ValidationError(string key, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] },
            title: "Validation request is invalid.");

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);

    private static bool ValidIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    private static bool ValidOptionalMetadata(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Length <= maximumLength && value.All(character => !char.IsControl(character));
}
