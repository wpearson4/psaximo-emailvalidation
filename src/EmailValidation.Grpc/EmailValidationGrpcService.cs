using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace EmailValidation.Grpc;

public sealed class EmailValidationGrpcService(
    IEmailValidator validator,
    IValidationStatusQueryService statuses,
    IValidationAccessPolicy accessPolicy,
    ICurrentConsumerContext consumers,
    ICommercialResourceStore resources,
    TimeProvider timeProvider,
    ILogger<EmailValidationGrpcService> logger) :
    EmailValidationService.EmailValidationServiceBase
{
    [Authorize(Policy = EmailValidationPolicies.Validate)]
    public override async Task<EmailValidationResponse> ValidateEmail(
        ValidateEmailRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 320)
            throw Error(StatusCode.InvalidArgument, "email is required and must not exceed 320 characters.", context);
        if (request.HasValidationId && !ValidId(request.ValidationId))
            throw Error(StatusCode.InvalidArgument, "validation_id is invalid.", context);

        try
        {
            var result = await validator.ValidateAsync(
                request.Email,
                new EmailValidationRequest(
                    !request.HasEnableSmtp || request.EnableSmtp,
                    request.Verbose,
                    request.HasValidationId ? request.ValidationId : null),
                context.CancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.ValidationId))
                throw new InvalidOperationException("The validation engine did not return a validation identifier.");
            var consumer = consumers.GetRequiredConsumer();
            await resources.GrantAsync(new ResourceOwnership(
                OwnedResourceType.Validation,
                result.ValidationId,
                consumer.PrincipalKey,
                consumer.SubjectId,
                consumer.TenantId,
                timeProvider.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
            return Map(result);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            logger.LogInformation(exception, "gRPC validation request was rejected");
            throw Error(StatusCode.InvalidArgument, "The validation request is invalid.", context);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "gRPC validation failed for trace {TraceId}", context.GetHttpContext().TraceIdentifier);
            throw Error(StatusCode.Internal, "The validation request could not be completed.", context);
        }
    }

    [Authorize(Policy = EmailValidationPolicies.Read)]
    public override async Task<EmailValidationResponse> GetValidation(
        GetValidationRequest request,
        ServerCallContext context)
    {
        if (!ValidId(request.ValidationId))
            throw Error(StatusCode.InvalidArgument, "validation_id is invalid.", context);
        var consumer = consumers.GetRequiredConsumer();
        var access = new ValidationAccessContext(
            consumer.SubjectId, consumer.TenantId, consumer.Scopes);
        if (!await accessPolicy.CanAccessAsync(request.ValidationId, access, context.CancellationToken)
                .ConfigureAwait(false))
            throw Error(StatusCode.PermissionDenied, "Validation is not accessible to this caller.", context);
        var snapshot = await statuses.GetAsync(request.ValidationId, context.CancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            throw Error(StatusCode.NotFound, "Validation was not found.", context);
        return Map(snapshot);
    }

    private static EmailValidationResponse Map(EmailValidationResult result)
    {
        var response = new EmailValidationResponse
        {
            ValidationId = result.ValidationId ?? string.Empty,
            Email = result.Email,
            LifecycleState = ResolveLifecycle(result),
            ResultState = result.ResultState.ToString(),
            Status = result.Status.ToString(),
            SubStatus = result.SubStatus.ToString(),
            Confidence = result.Confidence,
            Provider = result.MailProvider.ToString(),
            RetryScheduled = result.RetryScheduled,
            AttemptNumber = result.AttemptNumber,
            MaximumAttempts = result.MaximumAttempts,
            Source = result.Metadata?.ResultSource.ToString() ?? "LiveValidation"
        };
        if (!string.IsNullOrWhiteSpace(result.ConfidenceReason))
            response.ConfidenceReason = result.ConfidenceReason;
        if (result.UnknownContext is { } unknownContext)
            response.UnknownContext = MapUnknownContext(unknownContext);
        if (result.RetryAfter is { } retryAt)
            response.RetryAtUtc = Timestamp.FromDateTimeOffset(retryAt);
        if ((result.Metadata?.ValidatedAt ?? result.LastValidatedAt) is { } validatedAt)
            response.ValidatedAtUtc = Timestamp.FromDateTimeOffset(validatedAt);
        return response;
    }

    private static EmailValidationResponse Map(ValidationStatusSnapshot snapshot)
    {
        var response = new EmailValidationResponse
        {
            ValidationId = snapshot.ValidationId,
            Email = snapshot.Email ?? string.Empty,
            LifecycleState = snapshot.LifecycleState.ToString(),
            ResultState = snapshot.ResultState.ToString(),
            RetryScheduled = snapshot.RetryScheduled,
            AttemptNumber = snapshot.AttemptNumber,
            MaximumAttempts = snapshot.MaximumAttempts,
            Sequence = snapshot.Sequence
        };
        if (snapshot.Status is { } status) response.Status = status.ToString();
        if (snapshot.SubStatus is { } subStatus) response.SubStatus = subStatus.ToString();
        if (snapshot.Confidence is { } confidence) response.Confidence = confidence;
        if (!string.IsNullOrWhiteSpace(snapshot.ConfidenceReason)) response.ConfidenceReason = snapshot.ConfidenceReason;
        if (snapshot.UnknownContext is { } unknownContext)
            response.UnknownContext = MapUnknownContext(unknownContext);
        if (!string.IsNullOrWhiteSpace(snapshot.Provider)) response.Provider = snapshot.Provider;
        if (snapshot.RetryAt is { } retryAt) response.RetryAtUtc = Timestamp.FromDateTimeOffset(retryAt);
        if (snapshot.LastUpdatedAt is { } updated) response.ValidatedAtUtc = Timestamp.FromDateTimeOffset(updated);
        return response;
    }

    private static string ResolveLifecycle(EmailValidationResult result) =>
        result.ResultState == ValidationResultState.Final
            ? ValidationLifecycleState.Final.ToString()
            : result.RetryScheduled
                ? ValidationLifecycleState.RetryScheduled.ToString()
                : ValidationLifecycleState.Provisional.ToString();

    private static global::EmailValidation.V1.UnknownValidationContext MapUnknownContext(
        global::EmailValidation.Core.UnknownValidationContext context)
    {
        var response = new global::EmailValidation.V1.UnknownValidationContext
        {
            Cause = context.Cause.ToString(),
            Summary = context.Summary,
            Retryable = context.Retryable,
            RecommendedAction = context.RecommendedAction,
            SmtpCategory = context.SmtpCategory.ToString()
        };
        if (context.FailedStage is { } failedStage) response.FailedStage = failedStage.ToString();
        if (context.ResponseCode is { } responseCode) response.ResponseCode = responseCode;
        if (context.EnhancedStatusCode is { } enhancedStatus) response.EnhancedStatusCode = enhancedStatus;
        if (context.MxHost is { } mxHost) response.MxHost = mxHost;
        if (context.RetryAfter is { } retryAfter)
            response.RetryAfterUtc = Timestamp.FromDateTimeOffset(retryAfter);
        return response;
    }

    private static bool ValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    private static RpcException Error(StatusCode code, string detail, ServerCallContext context)
    {
        var metadata = new Metadata { { "x-trace-id", context.GetHttpContext().TraceIdentifier } };
        return new RpcException(new global::Grpc.Core.Status(code, detail), metadata);
    }
}
