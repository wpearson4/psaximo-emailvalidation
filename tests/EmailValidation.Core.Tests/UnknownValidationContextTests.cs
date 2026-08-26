using EmailValidation.Core;

namespace EmailValidation.Core.Tests;

public sealed class UnknownValidationContextTests
{
    private static readonly DateTimeOffset RetryAt = new(2026, 8, 25, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public void NonUnknownResult_DoesNotReceiveUnknownContext()
    {
        var result = Result(EmailValidationStatus.Valid, SmtpResponseCategory.Accepted);

        Assert.Null(UnknownValidationContextBuilder.Build(result));
    }

    [Fact]
    public void DisabledLiveValidation_ExplainsHowToObtainRecipientEvidence()
    {
        var context = UnknownValidationContextBuilder.Build(Result(
            EmailValidationStatus.Unknown,
            SmtpResponseCategory.NotAttempted,
            ReasonCode.SmtpDisabled));

        Assert.NotNull(context);
        Assert.Equal(UnknownCause.LiveVerificationDisabled, context.Cause);
        Assert.False(context.Retryable);
        Assert.Contains("Enable live SMTP", context.RecommendedAction, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeSenderFailure_TakesPrecedenceOverGenericSmtpDisabledReason()
    {
        var result = Result(
            EmailValidationStatus.Unknown,
            SmtpResponseCategory.NotAttempted,
            ReasonCode.SmtpDisabled,
            ReasonCode.ProbeSenderNotConfigured) with
        {
            ProbeSenderHealth = new(
                ProbeSenderHealthStatus.NotConfigured,
                null,
                null,
                "No authorized probe sender was returned by the configured source.")
        };

        var context = UnknownValidationContextBuilder.Build(result);

        Assert.NotNull(context);
        Assert.Equal(UnknownCause.ProbeSenderUnavailable, context.Cause);
        Assert.Contains("authorized probe sender", context.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalCooldown_CarriesRetryAndSmtpStageContext()
    {
        var result = Result(
            EmailValidationStatus.Unknown,
            SmtpResponseCategory.LocalCooldown,
            ReasonCode.LocalCooldown) with
        {
            RetryAfter = RetryAt,
            SelectedMx = "mx.example.test",
            SmtpEvidence = new(
                SmtpCommand.MailFrom,
                451,
                "4.7.1",
                SmtpResponseCategory.LocalCooldown,
                SmtpResponseTextClassification.TemporaryCondition,
                12,
                MailProvider.GenericSmtp,
                "mx.example.test",
                1,
                RetryAt.AddMinutes(-5))
        };

        var context = UnknownValidationContextBuilder.Build(result);

        Assert.NotNull(context);
        Assert.Equal(UnknownCause.LocalCooldown, context.Cause);
        Assert.True(context.Retryable);
        Assert.Equal(RetryAt, context.RetryAfter);
        Assert.Equal(SmtpCommand.MailFrom, context.FailedStage);
        Assert.Equal(451, context.ResponseCode);
        Assert.Equal("4.7.1", context.EnhancedStatusCode);
        Assert.Equal("mx.example.test", context.MxHost);
    }

    [Fact]
    public void ProviderBlock_ExplicitlyRejectsCircumvention()
    {
        var context = UnknownValidationContextBuilder.Build(Result(
            EmailValidationStatus.Unknown,
            SmtpResponseCategory.VerificationBlocked,
            ReasonCode.ProviderVerificationBlocked));

        Assert.NotNull(context);
        Assert.Equal(UnknownCause.ProviderVerificationBlocked, context.Cause);
        Assert.True(context.Retryable);
        Assert.Contains("do not attempt to circumvent", context.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    private static EmailValidationResult Result(
        EmailValidationStatus status,
        SmtpResponseCategory category,
        params ReasonCode[] reasons) => new()
        {
            Email = "person@example.test",
            NormalizedEmail = "person@example.test",
            Status = status,
            Confidence = .8,
            Checks = new EmailValidationChecks
            {
                SyntaxValid = true,
                DomainExists = true,
                MxPresent = true
            },
            ProviderValidation = new ProviderValidationResult(
                MailProvider.GenericSmtp,
                .55,
                category,
                AcceptanceStrength.None,
                reasons,
                "Test provider result."),
            ReasonCodes = reasons
        };
}
