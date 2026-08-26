using Google.Protobuf.WellKnownTypes;
using ApplicationLifecycleState = EmailValidation.Core.ValidationLifecycleState;
using ApplicationResultState = EmailValidation.Core.ValidationResultState;
using GrpcLifecycleState = EmailValidation.Status.V1.ValidationLifecycleState;
using GrpcResultState = EmailValidation.Status.V1.ValidationResultState;
using GrpcProgressStage = EmailValidation.Status.V1.ValidationProgressStage;
using CoreModel = EmailValidation.Core;

namespace EmailValidation.Grpc;

public static class ValidationStatusGrpcMapper
{
    public static Status.V1.ValidationStatusResponse Map(CoreModel.ValidationStatusChanged status)
    {
        var response = new Status.V1.ValidationStatusResponse
        {
            ValidationId = status.ValidationId,
            Sequence = status.Sequence,
            LifecycleState = Map(status.LifecycleState),
            CurrentStage = Map(status.CurrentStage),
            ResultState = status.ResultState == ApplicationResultState.Final
                ? GrpcResultState.Final
                : GrpcResultState.Provisional,
            AttemptNumber = status.AttemptNumber,
            MaximumAttempts = status.MaximumAttempts,
            RetryScheduled = status.RetryScheduled,
            OccurredAt = Timestamp.FromDateTimeOffset(status.OccurredAt),
            IsRunning = status.IsRunning,
            ResultReused = status.ResultReused
        };
        if (status.Status is { } validationStatus) response.Status = validationStatus.ToString();
        if (status.SubStatus is { } subStatus) response.SubStatus = subStatus.ToString();
        if (status.Confidence is { } confidence) response.Confidence = confidence;
        if (status.ConfidenceReason is not null) response.ConfidenceReason = status.ConfidenceReason;
        if (status.UnknownContext is { } unknownContext)
            response.UnknownContext = MapUnknownContext(unknownContext);
        if (status.RetryAt is { } retryAt) response.RetryAt = Timestamp.FromDateTimeOffset(retryAt);
        if (status.EstimatedRetryIn is { } retryIn) response.EstimatedRetryIn = Duration.FromTimeSpan(retryIn);
        if (status.Provider is not null) response.Provider = status.Provider;
        if (status.StatusMessage is not null) response.Message = status.StatusMessage;
        if (status.RetryReason is not null) response.RetryReason = status.RetryReason;
        if (status.CooldownUntil is { } cooldown) response.CooldownUntil = Timestamp.FromDateTimeOffset(cooldown);
        if (status.RequestedAt is { } requested) response.RequestedAt = Timestamp.FromDateTimeOffset(requested);
        if (status.StartedAt is { } started) response.StartedAt = Timestamp.FromDateTimeOffset(started);
        if (status.FirstValidatedAt is { } first) response.FirstValidatedAt = Timestamp.FromDateTimeOffset(first);
        if (status.LastUpdatedAt is { } updated) response.LastUpdatedAt = Timestamp.FromDateTimeOffset(updated);
        if (status.FinalizedAt is { } finalized) response.FinalizedAt = Timestamp.FromDateTimeOffset(finalized);
        return response;
    }

    private static Status.V1.UnknownValidationContext MapUnknownContext(
        CoreModel.UnknownValidationContext context)
    {
        var response = new Status.V1.UnknownValidationContext
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

    public static Status.V1.ValidationStatusResponse Map(CoreModel.ValidationStatusSnapshot snapshot, DateTimeOffset now)
        => Map(CoreModel.ValidationStatusMapper.ToEvent(snapshot, now));

    private static GrpcLifecycleState Map(ApplicationLifecycleState state) => state switch
    {
        ApplicationLifecycleState.Requested => GrpcLifecycleState.Requested,
        ApplicationLifecycleState.Validating => GrpcLifecycleState.Validating,
        ApplicationLifecycleState.Provisional => GrpcLifecycleState.Provisional,
        ApplicationLifecycleState.RetryScheduled => GrpcLifecycleState.RetryScheduled,
        ApplicationLifecycleState.RetryWaiting => GrpcLifecycleState.RetryWaiting,
        ApplicationLifecycleState.Revalidating => GrpcLifecycleState.Revalidating,
        ApplicationLifecycleState.Final => GrpcLifecycleState.Final,
        ApplicationLifecycleState.Failed => GrpcLifecycleState.Failed,
        _ => GrpcLifecycleState.Unspecified
    };

    private static GrpcProgressStage Map(CoreModel.ValidationProgressStage stage) => stage switch
    {
        CoreModel.ValidationProgressStage.Requested => GrpcProgressStage.Requested,
        CoreModel.ValidationProgressStage.Started => GrpcProgressStage.Started,
        CoreModel.ValidationProgressStage.DomainChecks => GrpcProgressStage.DomainChecks,
        CoreModel.ValidationProgressStage.ProviderChecks => GrpcProgressStage.ProviderChecks,
        CoreModel.ValidationProgressStage.SmtpValidation => GrpcProgressStage.SmtpValidation,
        CoreModel.ValidationProgressStage.PersistedIntelligence => GrpcProgressStage.PersistedIntelligence,
        CoreModel.ValidationProgressStage.Provisional => GrpcProgressStage.Provisional,
        CoreModel.ValidationProgressStage.RetryScheduled => GrpcProgressStage.RetryScheduled,
        CoreModel.ValidationProgressStage.RetryWaiting => GrpcProgressStage.RetryWaiting,
        CoreModel.ValidationProgressStage.Revalidating => GrpcProgressStage.Revalidating,
        CoreModel.ValidationProgressStage.Final => GrpcProgressStage.Final,
        CoreModel.ValidationProgressStage.Failed => GrpcProgressStage.Failed,
        _ => GrpcProgressStage.Unspecified
    };
}
