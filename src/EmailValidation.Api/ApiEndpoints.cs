using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EmailValidation.Api;

public static class ApiEndpoints
{
    private const string CreateJobOperation = "email-validation-jobs.create.v1";
    private const string CreateSourceFileJobOperation = "source-file-email-validation.create.v1";
    private const string CreatePurchasedJobOperation = "purchased-result-email-validation.create.v1";

    public static IEndpointRouteBuilder MapEmailValidationV1(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1")
            .WithTags("Email Validation v1");

        group.MapPost("/email-validations", ValidateEmailAsync)
            .WithName("CreateEmailValidationV1")
            .WithSummary("Validate one email address")
            .WithDescription("Runs the shared validation engine for an interactive user or explicitly authorized administrator. Standard machine clients must use the purchased-result endpoint. Mailbox invalidity is a successful HTTP operation.")
            .RequireAuthorization(
                EmailValidationPolicies.Validate,
                EmailValidationPolicies.ArbitraryValidation)
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
            .WithDescription("Creates an arbitrary-address job for an interactive user or explicitly authorized administrator. Standard machine clients must use the purchased-result endpoint. Idempotency-Key is supported and scoped to the authenticated consumer.")
            .RequireAuthorization(
                EmailValidationPolicies.JobsWrite,
                EmailValidationPolicies.ArbitraryValidation)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Accepts<CreateValidationJobV1Request>("application/json")
            .Produces<ValidationJobV1Response>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status429TooManyRequests);

        group.MapPost("/purchased-results/{transactionId}/email-validation", CreatePurchasedResultJobAsync)
            .WithName("CreatePurchasedResultEmailValidationV1")
            .WithSummary("Validate email data in a purchased Search or Match & Append result")
            .WithDescription("Authorizes the completed purchased result against the authenticated API client, reads the requested email column from the purchased output, and creates a durable asynchronous validation job. Submitted email addresses and client-asserted ownership are never accepted by this operation.")
            .RequireAuthorization(EmailValidationPolicies.PurchasedJobsWrite)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Accepts<CreatePurchasedResultValidationV1Request>("application/json")
            .Produces<ValidationJobV1Response>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ProblemDetails>(StatusCodes.Status429TooManyRequests)
            .Produces<ProblemDetails>(StatusCodes.Status502BadGateway)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/email-validation-files/{sourceFileId}/columns", DetectEmailColumnsAsync)
            .WithName("DetectEmailValidationColumnsV1")
            .WithSummary("Detect email columns in a purchased file")
            .WithDescription("Streams a bounded sample from the authorized source file and returns only columns confidently detected as email data.")
            .RequireAuthorization(EmailValidationPolicies.JobsWrite)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Produces<EmailColumnProfileV1Response>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ProblemDetails>(StatusCodes.Status429TooManyRequests)
            .Produces<ProblemDetails>(StatusCodes.Status502BadGateway)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/email-validation-files/{sourceFileId}/email-validation", CreateSourceFileJobAsync)
            .WithName("CreateSourceFileEmailValidationV1")
            .WithSummary("Validate email data in an authorized web source file")
            .WithDescription("Authorizes the selected Search or Match & Append output for the interactive user, streams the requested email column on the server, and creates a durable asynchronous validation job. Email addresses are not accepted from the browser.")
            .RequireAuthorization(
                EmailValidationPolicies.JobsWrite,
                EmailValidationPolicies.ArbitraryValidation)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Accepts<CreateSourceFileValidationV1Request>("application/json")
            .Produces<ValidationJobV1Response>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ProblemDetails>(StatusCodes.Status429TooManyRequests)
            .Produces<ProblemDetails>(StatusCodes.Status502BadGateway)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

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

        group.MapGet("/email-validation-jobs/{jobId}/file", DownloadValidatedFileAsync)
            .WithName("DownloadValidatedFileV1")
            .WithSummary("Download the source file with current validation results")
            .WithDescription("Reauthorizes the source file and streams a CSV that preserves every original row while appending the current validation result columns.")
            .RequireAuthorization(EmailValidationPolicies.JobsRead)
            .RequireRateLimiting(ApiRateLimitPolicies.Requests)
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status502BadGateway)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> DetectEmailColumnsAsync(
        string sourceFileId,
        HttpContext http,
        IEmailValidationSourceFileClient sourceFiles,
        IFileColumnProfiler profiler,
        ILoggerFactory loggerFactory,
        IOptions<ApiHostOptions> hostOptions,
        CancellationToken cancellationToken)
    {
        if (!ValidIdentifier(sourceFileId, hostOptions.Value.Limits.MaximumIdentifierLength))
            return ValidationError("sourceFileId", "SourceFileId is invalid.");

        try
        {
            await using var source = await sourceFiles.OpenAsync(
                sourceFileId,
                http.Request.Headers.Authorization.ToString(),
                cancellationToken).ConfigureAwait(false);
            var profile = await profiler.ProfileAsync(
                source.Content, source.FileName, cancellationToken).ConfigureAwait(false);
            var detected = profile.Columns
                .Where(column => column.DetectedType == DetectedColumnType.Email)
                .Select(column => new DetectedEmailColumnV1Response(
                    column.ColumnName,
                    column.DetectedType.ToString(),
                    Math.Round(column.Confidence, 4)))
                .ToArray();

            var logger = loggerFactory.CreateLogger("EmailValidation.ColumnDetection");
            foreach (var column in profile.Columns)
                logger.LogInformation(
                    "Email column profile SourceFileId={SourceFileId} ColumnName={ColumnName} SampleCount={SampleCount} NonEmptySampleCount={NonEmptySampleCount} DetectedType={DetectedType} DetectionConfidence={DetectionConfidence:F4}",
                    sourceFileId,
                    column.ColumnName,
                    column.SampleCount,
                    column.NonEmptySampleCount,
                    column.DetectedType,
                    column.Confidence);

            return Results.Ok(new EmailColumnProfileV1Response(
                sourceFileId, source.FileName, detected));
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return Results.Forbid();
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Results.Unauthorized();
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Problem(StatusCodes.Status404NotFound, "Source file not found",
                "The selected purchased file is no longer available.");
        }
        catch (SourceFileAccessException)
        {
            return Problem(StatusCodes.Status502BadGateway, "Source file unavailable",
                "The purchased-file service could not provide the selected file.");
        }
        catch (HttpRequestException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "Source file service unavailable",
                "The purchased-file service is temporarily unavailable.");
        }
        catch (InvalidDataException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Source file could not be profiled", exception.Message);
        }
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

        var consumer = consumers.GetRequiredConsumer();
        var result = await validator.ValidateAsync(input.Email,
            new EmailValidationRequest(input.EnableSmtp, input.Verbose, input.ValidationId,
                consumer.TenantId, consumer.SubjectId), cancellationToken)
            .ConfigureAwait(false);
        var response = ApiContractMapper.Map(result);
        await resources.GrantAsync(new ResourceOwnership(
            OwnedResourceType.Validation,
            response.ValidationId,
            consumer.PrincipalKey,
            consumer.SubjectId,
            consumer.TenantId,
            timeProvider.GetUtcNow(),
            consumer.ActorSubjectId,
            consumer.ImpersonationSessionId), CancellationToken.None).ConfigureAwait(false);
        return Results.Ok(response);
    }

    private static async Task<IResult> CreateSourceFileJobAsync(
        string sourceFileId,
        HttpContext http,
        CreateSourceFileValidationV1Request? input,
        IEmailValidationSourceFileClient sourceFiles,
        IPurchasedEmailDataReader emailData,
        IValidationJobService jobs,
        ICurrentConsumerContext consumers,
        ICommercialResourceStore resources,
        TimeProvider timeProvider,
        IOptions<ApiHostOptions> hostOptions,
        IOptions<EmailValidationOptions> engineOptions,
        CancellationToken cancellationToken)
    {
        var limits = hostOptions.Value.Limits;
        if (!ValidIdentifier(sourceFileId, limits.MaximumIdentifierLength))
            return ValidationError("sourceFileId", "SourceFileId is invalid.");
        if (input is null || string.IsNullOrWhiteSpace(input.EmailColumn) ||
            !ValidOptionalMetadata(input.EmailColumn, 256))
            return ValidationError("emailColumn", "EmailColumn is required and must be valid.");

        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (!ValidIdempotencyKey(key, limits))
            return ValidationError("Idempotency-Key", "Idempotency-Key is invalid or too long.");
        var consumer = consumers.GetRequiredConsumer();
        var requestHash = IdempotencyRequestHasher.HashSourceFileRequest(
            sourceFileId, input.EmailColumn, input.EnableSmtp);
        var replay = await ReadIdempotentReplayAsync(
            key,
            requestHash,
            CreateSourceFileJobOperation,
            consumer,
            jobs,
            resources,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;

        try
        {
            await using var source = await sourceFiles.OpenAsync(
                sourceFileId,
                http.Request.Headers.Authorization.ToString(),
                cancellationToken).ConfigureAwait(false);
            var sourceEmailData = await emailData.ReadAsync(
                source.Content,
                source.FileName,
                input.EmailColumn,
                engineOptions.Value.Jobs.MaximumItemsPerJob,
                cancellationToken).ConfigureAwait(false);

            return await SubmitJobAsync(
                http,
                new PreparedValidationJob(
                    sourceEmailData.Emails,
                    input.EnableSmtp,
                    sourceFileId,
                    source.FileName,
                    input.EmailColumn.Trim(),
                    sourceEmailData.SourcePositions,
                    requestHash),
                CreateSourceFileJobOperation,
                jobs,
                consumers,
                resources,
                timeProvider,
                hostOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Results.Unauthorized();
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return Results.Forbid();
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Problem(StatusCodes.Status404NotFound, "Source file not found",
                "The selected purchased file is no longer available.");
        }
        catch (SourceFileAccessException exception) when (
            exception.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "Source file is too large",
                "The selected file exceeds the configured Email Validation limit.",
                "SOURCE_FILE_TOO_LARGE");
        }
        catch (PurchasedEmailColumnNotFoundException)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Email column is unavailable",
                "The selected email column is no longer present exactly once in the source file.",
                "EMAIL_COLUMN_NOT_FOUND");
        }
        catch (PurchasedEmailColumnEmptyException)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Email column is empty",
                "The selected email column contains no values to validate.",
                "EMAIL_COLUMN_EMPTY");
        }
        catch (PurchasedEmailLimitExceededException exception)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "Source file is too large",
                exception.Message, "SOURCE_FILE_TOO_LARGE");
        }
        catch (SourceFileSizeLimitExceededException)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "Source file is too large",
                "The selected file exceeds the configured Email Validation limit.",
                "SOURCE_FILE_TOO_LARGE");
        }
        catch (InvalidDataException)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Source file is invalid",
                "The selected file could not be read as a supported validation source.",
                "SOURCE_FILE_INVALID");
        }
        catch (HttpRequestException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "Source file service unavailable",
                "The purchased-file service is temporarily unavailable.", "SERVICE_UNAVAILABLE");
        }
        catch (SourceFileAccessException)
        {
            return Problem(StatusCodes.Status502BadGateway, "Source file unavailable",
                "The purchased-file service could not provide the selected file.",
                "SOURCE_FILE_UNAVAILABLE");
        }
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
        var validationInputs = input.Emails
            .Select((email, position) => new { Email = email?.Trim(), Position = position })
            .Where(item => !string.IsNullOrWhiteSpace(item.Email))
            .ToArray();
        if (validationInputs.Length == 0)
            return ValidationError("emails", "At least one non-empty email is required.");
        if (validationInputs.Any(item => item.Email!.Length > limits.MaximumEmailLength))
            return ValidationError("emails", $"Each email must not exceed {limits.MaximumEmailLength} characters.");
        if (validationInputs.Length > engineOptions.Value.Jobs.MaximumItemsPerJob)
            return ValidationError("emails",
                $"A job may contain at most {engineOptions.Value.Jobs.MaximumItemsPerJob} items.");
        var emails = validationInputs.Select(item => item.Email!).ToArray();
        var sourcePositions = validationInputs.Select(item => item.Position).ToArray();
        if (!ValidOptionalMetadata(input.SourceFileId, 256) ||
            !ValidOptionalMetadata(input.SourceFileName, 512) ||
            !ValidOptionalMetadata(input.EmailColumn, 256))
            return ValidationError("source", "Source file metadata contains unsupported characters or is too long.");

        return await SubmitJobAsync(
            http,
            new PreparedValidationJob(
                emails,
                input.EnableSmtp,
                input.SourceFileId?.Trim(),
                input.SourceFileName?.Trim(),
                input.EmailColumn?.Trim(),
                sourcePositions),
            CreateJobOperation,
            jobs,
            consumers,
            resources,
            timeProvider,
            hostOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> CreatePurchasedResultJobAsync(
        string transactionId,
        HttpContext http,
        CreatePurchasedResultValidationV1Request? input,
        IPurchasedResultClient purchasedResults,
        IPurchasedEmailDataReader emailData,
        IValidationJobService jobs,
        ICurrentConsumerContext consumers,
        ICommercialResourceStore resources,
        TimeProvider timeProvider,
        IOptions<ApiHostOptions> hostOptions,
        IOptions<EmailValidationOptions> engineOptions,
        CancellationToken cancellationToken)
    {
        var limits = hostOptions.Value.Limits;
        if (!ValidIdentifier(transactionId, limits.MaximumIdentifierLength))
            return ValidationError("transactionId", "TransactionId is invalid.");
        if (input is null || string.IsNullOrWhiteSpace(input.EmailColumn) ||
            !ValidOptionalMetadata(input.EmailColumn, 256))
            return ValidationError("emailColumn", "EmailColumn is required and must be valid.");

        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (!ValidIdempotencyKey(key, limits))
            return ValidationError("Idempotency-Key", "Idempotency-Key is invalid or too long.");
        var consumer = consumers.GetRequiredConsumer();
        var requestHash = IdempotencyRequestHasher.HashPurchasedResultRequest(
            transactionId, input.EmailColumn, input.EnableSmtp);
        var replay = await ReadIdempotentReplayAsync(
            key,
            requestHash,
            CreatePurchasedJobOperation,
            consumer,
            jobs,
            resources,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;

        try
        {
            await using var source = await purchasedResults.OpenAsync(
                transactionId,
                http.Request.Headers.Authorization.ToString(),
                cancellationToken).ConfigureAwait(false);
            var purchasedEmailData = await emailData.ReadAsync(
                source.Content,
                source.FileName,
                input.EmailColumn,
                engineOptions.Value.Jobs.MaximumItemsPerJob,
                cancellationToken).ConfigureAwait(false);

            return await SubmitJobAsync(
                http,
                new PreparedValidationJob(
                    purchasedEmailData.Emails,
                    input.EnableSmtp,
                    transactionId,
                    source.FileName,
                    input.EmailColumn.Trim(),
                    purchasedEmailData.SourcePositions,
                    requestHash),
                CreatePurchasedJobOperation,
                jobs,
                consumers,
                resources,
                timeProvider,
                hostOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Results.Unauthorized();
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return Problem(StatusCodes.Status403Forbidden, "Email validation is not authorized",
                "The access token cannot validate this purchased result.", "EMAIL_VALIDATION_NOT_AUTHORIZED");
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Problem(StatusCodes.Status404NotFound, "Purchased result not found",
                "The purchased result does not exist or is not available to this API client.",
                "PURCHASED_RESULT_NOT_FOUND");
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return Problem(StatusCodes.Status409Conflict, "Purchase is not complete",
                "Email Validation is available only after the Search or Match & Append purchase is complete.",
                "PURCHASE_NOT_COMPLETED");
        }
        catch (SourceFileAccessException exception) when (
            exception.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "Purchased result is too large",
                "The purchased result exceeds the configured Email Validation limit.",
                "PURCHASED_RESULT_TOO_LARGE");
        }
        catch (PurchasedEmailColumnNotFoundException)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Email was not included in the purchase",
                "The selected field is not present in the purchased output.",
                "EMAIL_NOT_INCLUDED_IN_PURCHASE");
        }
        catch (PurchasedEmailColumnEmptyException)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Purchased email field is empty",
                "The selected purchased email field contains no values to validate.",
                "EMAIL_NOT_INCLUDED_IN_PURCHASE");
        }
        catch (PurchasedEmailLimitExceededException exception)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "Purchased result is too large",
                exception.Message, "PURCHASED_RESULT_TOO_LARGE");
        }
        catch (SourceFileSizeLimitExceededException)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "Purchased result is too large",
                "The purchased result exceeds the configured Email Validation limit.",
                "PURCHASED_RESULT_TOO_LARGE");
        }
        catch (InvalidDataException)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Purchased result is invalid",
                "The purchased result could not be read as a supported validation source.",
                "PURCHASED_RESULT_INVALID");
        }
        catch (HttpRequestException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "Purchased-result service unavailable",
                "The purchased-result service is temporarily unavailable.", "SERVICE_UNAVAILABLE");
        }
        catch (SourceFileAccessException)
        {
            return Problem(StatusCodes.Status502BadGateway, "Purchased result unavailable",
                "The purchased-result service could not provide the selected result.",
                "PURCHASED_RESULT_UNAVAILABLE");
        }
    }

    private static async Task<IResult> SubmitJobAsync(
        HttpContext http,
        PreparedValidationJob input,
        string operation,
        IValidationJobService jobs,
        ICurrentConsumerContext consumers,
        ICommercialResourceStore resources,
        TimeProvider timeProvider,
        IOptions<ApiHostOptions> hostOptions,
        CancellationToken cancellationToken)
    {
        var consumer = consumers.GetRequiredConsumer();
        var jobTenantId = consumer.TenantId ?? consumer.PrincipalKey;
        var sourceFileId = input.SourceFileId;
        var limits = hostOptions.Value.Limits;
        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (!ValidIdempotencyKey(key, limits))
            return ValidationError("Idempotency-Key", "Idempotency-Key is invalid or too long.");

        var hash = input.RequestHash ?? IdempotencyRequestHasher.HashJobRequest(
            input.Emails, input.EnableSmtp, sourceFileId, input.EmailColumn, input.SourcePositions);
        var replay = await ReadIdempotentReplayAsync(
            key,
            hash,
            operation,
            consumer,
            jobs,
            resources,
            cancellationToken,
            input.RequestHash is null ? input : null,
            jobTenantId).ConfigureAwait(false);
        if (replay is not null) return replay;

        ValidationJobSnapshot? sourceJob = null;
        if (!string.IsNullOrWhiteSpace(sourceFileId))
        {
            sourceJob = await jobs.GetBySourceFileIdAsync(
                sourceFileId, jobTenantId, cancellationToken).ConfigureAwait(false);
            if (sourceJob?.State is ValidationJobState.Completed or ValidationJobState.CompletedWithErrors)
                return Problem(StatusCodes.Status409Conflict, "File already validated",
                    "This source file already has a completed validation job.",
                    "EMAIL_VALIDATION_ALREADY_COMPLETED");
        }

        var jobId = string.IsNullOrWhiteSpace(sourceFileId)
            ? Guid.NewGuid().ToString("N")
            : sourceJob?.JobId ?? ValidationJobIdentity.FromSourceFileId(sourceFileId, jobTenantId);
        if (!string.IsNullOrEmpty(key))
        {
            var saved = await resources.TrySaveIdempotentOperationAsync(new IdempotentOperation(
                consumer.PrincipalKey, operation, key, hash, jobId, timeProvider.GetUtcNow()), cancellationToken)
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
                    input.SourceFileName,
                    input.EmailColumn,
                    input.SourcePositions,
                    TenantId: jobTenantId,
                    ActorUserId: consumer.ActorSubjectId,
                    ImpersonationSessionId: consumer.ImpersonationSessionId),
                CancellationToken.None)
                .ConfigureAwait(false);
            await resources.GrantAsync(new ResourceOwnership(
                OwnedResourceType.ValidationJob,
                job.JobId,
                consumer.PrincipalKey,
                consumer.SubjectId,
                consumer.TenantId,
                timeProvider.GetUtcNow(),
                consumer.ActorSubjectId,
                consumer.ImpersonationSessionId), CancellationToken.None).ConfigureAwait(false);
            return Results.Accepted($"/v1/email-validation-jobs/{job.JobId}", ApiContractMapper.Map(job));
        }
        catch (ArgumentException exception)
        {
            return ValidationError("emails", exception.Message);
        }
        catch (ValidationJobSourceFileCompletedException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "File already validated", exception.Message,
                "EMAIL_VALIDATION_ALREADY_COMPLETED");
        }
        catch (ValidationJobSourceFileActiveException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "File validation already in progress", exception.Message,
                "EMAIL_VALIDATION_ALREADY_RUNNING");
        }
    }

    private sealed record PreparedValidationJob(
        IReadOnlyList<string> Emails,
        bool EnableSmtp,
        string? SourceFileId,
        string? SourceFileName,
        string? EmailColumn,
        IReadOnlyList<int> SourcePositions,
        string? RequestHash = null);

    private static async Task<IResult?> ReadIdempotentReplayAsync(
        string key,
        string requestHash,
        string operation,
        CurrentConsumer consumer,
        IValidationJobService jobs,
        ICommercialResourceStore resources,
        CancellationToken cancellationToken,
        PreparedValidationJob? failedRetry = null,
        string? jobTenantId = null)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var existing = await resources.GetIdempotentOperationAsync(
            consumer.PrincipalKey, operation, key, cancellationToken).ConfigureAwait(false);
        if (existing is null) return null;
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            return Problem(StatusCodes.Status409Conflict, "Idempotency conflict",
                "The Idempotency-Key was already used with a different request.",
                "IDEMPOTENCY_CONFLICT");
        var existingJob = await jobs.GetAsync(existing.ResourceId, cancellationToken).ConfigureAwait(false);
        if (existingJob is null)
            return Problem(StatusCodes.Status409Conflict, "Job creation in progress",
                "The idempotent operation is still being created. Retry shortly.",
                "IDEMPOTENT_OPERATION_PENDING");
        if (existingJob.State == ValidationJobState.Failed &&
            failedRetry is not null &&
            !string.IsNullOrWhiteSpace(failedRetry.SourceFileId))
        {
            existingJob = await jobs.CreateAsync(new CreateValidationJobRequest(
                failedRetry.Emails,
                failedRetry.EnableSmtp,
                existingJob.JobId,
                failedRetry.SourceFileId,
                failedRetry.SourceFileName,
                failedRetry.EmailColumn,
                failedRetry.SourcePositions,
                TenantId: jobTenantId,
                ActorUserId: consumer.ActorSubjectId,
                ImpersonationSessionId: consumer.ImpersonationSessionId), CancellationToken.None).ConfigureAwait(false);
        }
        return Results.Accepted($"/v1/email-validation-jobs/{existingJob.JobId}",
            ApiContractMapper.Map(existingJob));
    }

    private static bool ValidIdempotencyKey(string key, ApiLimitsOptions limits) =>
        string.IsNullOrEmpty(key) ||
        key.Length <= limits.MaximumIdempotencyKeyLength && !key.Any(char.IsControl);

    private static async Task<IResult> ListJobsAsync(
        int? skip,
        int? take,
        HttpContext http,
        IValidationJobService jobs,
        ICommercialResourceStore resources,
        IEmailValidationSourceFileClient sourceFiles,
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
        var visible = await Task.WhenAll(snapshots
            .Where(snapshot => snapshot is not null)
            .Select(async snapshot => await HasCurrentSourceAccessAsync(
                    snapshot!, http, sourceFiles, cancellationToken).ConfigureAwait(false)
                ? snapshot
                : null)).ConfigureAwait(false);
        var items = visible.Where(snapshot => snapshot is not null)
            .Select(snapshot => ApiContractMapper.Map(snapshot!))
            .ToArray();
        var nextSkip = owned.Count > actualTake ? actualSkip + actualTake : (int?)null;
        return Results.Ok(new ValidationJobPageV1Response(actualSkip, actualTake, items, nextSkip));
    }

    private static async Task<IResult> GetJobAsync(
        string jobId,
        HttpContext http,
        IValidationJobService jobs,
        IValidationJobAccessPolicy accessPolicy,
        IEmailValidationSourceFileClient sourceFiles,
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
        if (job is null)
            return Problem(StatusCodes.Status404NotFound, "Job not found", "The validation job does not exist.");
        return await HasCurrentSourceAccessAsync(job, http, sourceFiles, cancellationToken).ConfigureAwait(false)
            ? Results.Ok(ApiContractMapper.Map(job))
            : Results.Forbid();
    }

    private static async Task<IResult> GetJobResultsAsync(
        string jobId,
        int? skip,
        int? take,
        HttpContext http,
        IValidationJobService jobs,
        IValidationJobAccessPolicy accessPolicy,
        IEmailValidationSourceFileClient sourceFiles,
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
        if (!await HasCurrentSourceAccessAsync(job, http, sourceFiles, cancellationToken).ConfigureAwait(false))
            return Results.Forbid();
        var actualSkip = skip ?? 0;
        var actualTake = take ?? limits.DefaultJobResultPageSize;
        var items = await jobs.GetResultsAsync(jobId, actualSkip, actualTake, cancellationToken).ConfigureAwait(false);
        int? next = actualSkip + items.Count < job.TotalItems ? actualSkip + items.Count : null;
        return Results.Ok(new ValidationJobResultsPageV1Response(
            jobId, actualSkip, actualTake, items.Select(ApiContractMapper.Map).ToArray(), next));
    }

    private static async Task<IResult> DownloadValidatedFileAsync(
        string jobId,
        HttpContext http,
        IValidationJobService jobs,
        IValidationJobAccessPolicy accessPolicy,
        IEmailValidationSourceFileClient sourceFiles,
        ICurrentConsumerContext consumers,
        ValidationJobCsvExporter exporter,
        IOptions<ApiHostOptions> hostOptions,
        CancellationToken cancellationToken)
    {
        if (!ValidIdentifier(jobId, hostOptions.Value.Limits.MaximumIdentifierLength))
            return ValidationError("jobId", "JobId is invalid.");
        if (!await accessPolicy.CanAccessAsync(jobId, consumers.GetRequiredConsumer(), cancellationToken)
                .ConfigureAwait(false))
            return Results.Forbid();
        var job = await jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return Problem(StatusCodes.Status404NotFound, "Job not found", "The validation job does not exist.");
        if (job.State is not (ValidationJobState.Completed or ValidationJobState.CompletedWithErrors))
            return Problem(StatusCodes.Status409Conflict, "Validated file is not ready",
                "Wait for Email Validation to finish before downloading the validated file.",
                "VALIDATED_FILE_NOT_READY");
        if (string.IsNullOrWhiteSpace(job.SourceFileId))
            return Problem(StatusCodes.Status409Conflict, "Source file is unavailable",
                "This validation job is not associated with a downloadable source file.",
                "SOURCE_FILE_UNAVAILABLE");

        try
        {
            await sourceFiles.DemandAccessAsync(
                job.SourceFileId,
                http.Request.Headers.Authorization.ToString(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Results.Unauthorized();
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return Results.Forbid();
        }
        catch (SourceFileAccessException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Problem(StatusCodes.Status404NotFound, "Source file not found",
                "The source file for this validation job is no longer available.");
        }
        catch (HttpRequestException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "Source file service unavailable",
                "The purchased-file service is temporarily unavailable.", "SERVICE_UNAVAILABLE");
        }
        catch (SourceFileAccessException)
        {
            return Problem(StatusCodes.Status502BadGateway, "Source file unavailable",
                "The purchased-file service could not authorize the source file.",
                "SOURCE_FILE_UNAVAILABLE");
        }

        var outputName = Path.GetFileNameWithoutExtension(job.SourceFileName ?? "validated-file") +
            "-email-validated.csv";
        var authorization = http.Request.Headers.Authorization.ToString();
        return Results.Stream(async output =>
        {
            await using var source = await sourceFiles.OpenAsync(
                job.SourceFileId,
                authorization,
                http.RequestAborted).ConfigureAwait(false);
            await exporter.WriteAsync(
                source.Content,
                source.FileName,
                job,
                output,
                http.RequestAborted).ConfigureAwait(false);
        }, "text/csv; charset=utf-8", outputName);
    }

    private static async Task<bool> HasCurrentSourceAccessAsync(
        ValidationJobSnapshot job,
        HttpContext http,
        IEmailValidationSourceFileClient sourceFiles,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.SourceFileId)
            || ApiSecurityExtensions.IsMachineClient(http.User))
            return true;
        try
        {
            await sourceFiles.DemandAccessAsync(
                job.SourceFileId,
                http.Request.Headers.Authorization.ToString(),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SourceFileAccessException)
        {
            return false;
        }
    }

    private static IResult ValidationError(string key, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] },
            title: "Validation request is invalid.");

    private static IResult Problem(int status, string title, string detail, string? code = null) =>
        Results.Problem(
            statusCode: status,
            title: title,
            detail: detail,
            type: code is null
                ? null
                : $"https://email.digitalwarehouse.io/problems/{code.ToLowerInvariant().Replace('_', '-')}",
            extensions: code is null
                ? null
                : new Dictionary<string, object?> { ["code"] = code });

    private static bool ValidIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    private static bool ValidOptionalMetadata(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Length <= maximumLength && value.All(character => !char.IsControl(character));
}
