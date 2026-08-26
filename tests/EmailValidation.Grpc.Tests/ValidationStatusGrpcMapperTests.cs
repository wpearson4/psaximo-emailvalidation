using EmailValidation.Core;
using EmailValidation.Grpc;

namespace EmailValidation.Grpc.Tests;

public sealed class ValidationStatusGrpcMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 14, 15, 0, TimeSpan.Zero);

    [Fact]
    public void MapsRetryTimingFinalityConfidenceProviderAndSequence()
    {
        var status = new ValidationStatusChanged
        {
            ValidationId = "validation-123",
            LifecycleState = ValidationLifecycleState.RetryWaiting,
            CurrentStage = ValidationProgressStage.RetryWaiting,
            ResultState = ValidationResultState.Provisional,
            RetryScheduled = true,
            RetryAt = Now.AddMinutes(45),
            EstimatedRetryIn = TimeSpan.FromMinutes(45),
            RetryReason = ReasonCode.ProviderVerificationBlocked.ToString(),
            Provider = MailProvider.Microsoft365.ToString(),
            Confidence = 0.25,
            UnknownContext = new(
                UnknownCause.ProviderVerificationBlocked,
                "The provider blocked recipient verification.",
                true,
                "Wait for the provider cooldown and retry.",
                SmtpResponseCategory.VerificationBlocked,
                SmtpCommand.MailFrom,
                550,
                "5.7.1",
                "mx.example.com",
                Now.AddMinutes(45)),
            AttemptNumber = 1,
            MaximumAttempts = 2,
            Sequence = 9,
            OccurredAt = Now
        };

        var response = ValidationStatusGrpcMapper.Map(status);

        Assert.Equal(9, response.Sequence);
        Assert.Equal(Status.V1.ValidationLifecycleState.RetryWaiting, response.LifecycleState);
        Assert.Equal(Status.V1.ValidationProgressStage.RetryWaiting, response.CurrentStage);
        Assert.Equal(Status.V1.ValidationResultState.Provisional, response.ResultState);
        Assert.Equal(Now.AddMinutes(45), response.RetryAt.ToDateTimeOffset());
        Assert.Equal(TimeSpan.FromMinutes(45), response.EstimatedRetryIn.ToTimeSpan());
        Assert.Equal(0.25, response.Confidence);
        Assert.Equal("Microsoft365", response.Provider);
        Assert.Equal(2, response.MaximumAttempts);
        Assert.Equal("ProviderVerificationBlocked", response.UnknownContext.Cause);
        Assert.True(response.UnknownContext.Retryable);
        Assert.Equal("MailFrom", response.UnknownContext.FailedStage);
        Assert.Equal(550, response.UnknownContext.ResponseCode);
        Assert.Equal(Now.AddMinutes(45), response.UnknownContext.RetryAfterUtc.ToDateTimeOffset());
    }

    [Fact]
    public void MapsRetryScheduledAsDistinctCanonicalState()
    {
        var response = ValidationStatusGrpcMapper.Map(new ValidationStatusChanged
        {
            ValidationId = "validation-scheduling",
            LifecycleState = ValidationLifecycleState.RetryScheduled,
            CurrentStage = ValidationProgressStage.RetryScheduled,
            ResultState = ValidationResultState.Provisional,
            Sequence = 4,
            OccurredAt = Now
        });

        Assert.Equal(Status.V1.ValidationLifecycleState.RetryScheduled, response.LifecycleState);
        Assert.Equal(Status.V1.ValidationProgressStage.RetryScheduled, response.CurrentStage);
    }
}
