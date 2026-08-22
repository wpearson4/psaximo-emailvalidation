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

    public static Status.V1.ValidationStatusResponse Map(CoreModel.ValidationStatusSnapshot snapshot, DateTimeOffset now)
        => Map(CoreModel.ValidationStatusMapper.ToEvent(snapshot, now));

    private static GrpcLifecycleState Map(ApplicationLifecycleState state) => state switch
    {
        ApplicationLifecycleState.Requested => GrpcLifecycleState.Requested,
        ApplicationLifecycleState.Validating => GrpcLifecycleState.Validating,
        ApplicationLifecycleState.Provisional => GrpcLifecycleState.Provisional,
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
        CoreModel.ValidationProgressStage.RetryWaiting => GrpcProgressStage.RetryWaiting,
        CoreModel.ValidationProgressStage.Revalidating => GrpcProgressStage.Revalidating,
        CoreModel.ValidationProgressStage.Final => GrpcProgressStage.Final,
        CoreModel.ValidationProgressStage.Failed => GrpcProgressStage.Failed,
        _ => GrpcProgressStage.Unspecified
    };
}
