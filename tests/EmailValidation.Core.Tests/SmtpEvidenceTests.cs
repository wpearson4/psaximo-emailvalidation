using EmailValidation.Core;
using EmailValidation.Infrastructure;

namespace EmailValidation.Core.Tests;

public sealed class SmtpEvidenceTests
{
    private readonly SmtpResponseClassifier _classifier = new();

    [Theory]
    [InlineData(250, "250 2.1.5 OK", "2.1.5", SmtpResponseCategory.Accepted, SmtpResponseTextClassification.Success)]
    [InlineData(550, "550 5.1.1 User unknown", "5.1.1", SmtpResponseCategory.RecipientRejected, SmtpResponseTextClassification.RecipientDoesNotExist)]
    [InlineData(550, "550 5.7.1 Rejected by policy", "5.7.1", SmtpResponseCategory.VerificationBlocked, SmtpResponseTextClassification.PolicyRejection)]
    [InlineData(451, "451 4.7.1 Greylisted", "4.7.1", SmtpResponseCategory.Greylisted, SmtpResponseTextClassification.Greylisting)]
    [InlineData(421, "421 4.7.0 Rate limit exceeded", "4.7.0", SmtpResponseCategory.RateLimited, SmtpResponseTextClassification.RateLimit)]
    [InlineData(450, "450 4.2.0 Temporarily unavailable", "4.2.0", SmtpResponseCategory.TemporaryFailure, SmtpResponseTextClassification.TemporaryCondition)]
    public void Classify_ParsesEnhancedStatusAndNormalizedCategory(
        int code,
        string response,
        string enhanced,
        SmtpResponseCategory category,
        SmtpResponseTextClassification textClassification)
    {
        var result = _classifier.Classify(
            SmtpCommand.RcptTo, code, response, TimeSpan.FromMilliseconds(12),
            MailProvider.GenericSmtp, "mx.example.com");

        Assert.Equal(enhanced, result.EnhancedStatusCode);
        Assert.Equal(category, result.Category);
        Assert.Equal(textClassification, result.TextClassification);
        Assert.Equal(12, result.ElapsedMilliseconds);
    }

    [Fact]
    public void Classify_RedactsEmailAddressesFromStoredResponse()
    {
        var result = _classifier.Classify(
            SmtpCommand.RcptTo, 550, "550 5.1.1 user@example.com does not exist", TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.com");

        Assert.DoesNotContain("user@example.com", result.SanitizedResponse, StringComparison.Ordinal);
        Assert.Contains("<redacted-email>", result.SanitizedResponse, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_MailFromRejectionDoesNotBecomeMailboxRejection()
    {
        var result = _classifier.Classify(
            SmtpCommand.MailFrom, 550, "550 5.7.1 Sender rejected", TimeSpan.Zero,
            MailProvider.Microsoft365, "mx.example.com");

        Assert.Equal(SmtpResponseCategory.VerificationBlocked, result.Category);
    }

    [Fact]
    public void Classify_GreetingSuccess_IsRecordedAsAcceptedStage()
    {
        var result = _classifier.Classify(
            SmtpCommand.Greeting, 220, "220 mx.example.com ESMTP ready", TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.com");

        Assert.Equal(SmtpResponseCategory.Accepted, result.Category);
    }

    [Fact]
    public void Classify_Unexplained550IsConservativeNotRecipientDefinitive()
    {
        var result = _classifier.Classify(
            SmtpCommand.RcptTo, 550, "550 Requested action not taken", TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.com");

        Assert.Equal(SmtpResponseCategory.VerificationBlocked, result.Category);
    }

    [Fact]
    public void Classify_MicrosoftEopRecipientSpecific541_IsRecipientRejected()
    {
        var result = _classifier.Classify(
            SmtpCommand.RcptTo, 550, "550 5.4.1 Recipient address rejected: Access denied.", TimeSpan.Zero,
            MailProvider.Microsoft365, "tenant.mail.protection.outlook.com");

        Assert.Equal(SmtpResponseCategory.RecipientRejected, result.Category);
        Assert.Equal(SmtpResponseTextClassification.RecipientDoesNotExist, result.TextClassification);
    }

    [Fact]
    public void Classify_MicrosoftConsumerRecipientSpecific541_IsRecipientRejected()
    {
        var result = _classifier.Classify(
            SmtpCommand.RcptTo, 550, "550 5.4.1 Recipient address rejected: Access denied.", TimeSpan.Zero,
            MailProvider.MicrosoftConsumer, "outlook-com.olc.protection.outlook.com");

        Assert.Equal(SmtpResponseCategory.RecipientRejected, result.Category);
        Assert.Equal(SmtpResponseTextClassification.RecipientDoesNotExist, result.TextClassification);
    }

    [Theory]
    [InlineData(421, "421 4.7.28 Gmail has detected an unusual rate of email; temporarily blocked")]
    [InlineData(451, "451 4.7.23 The sending IP address does not have a PTR record")]
    [InlineData(421, "421 4.7.0 Suspicious traffic; try again later")]
    public void Classify_Google47x_IsPolicyInconclusiveNeverRecipientRejected(int code, string response)
    {
        var result = _classifier.Classify(
            SmtpCommand.RcptTo, code, response, TimeSpan.Zero,
            MailProvider.GoogleWorkspace, "gmail-smtp-in.l.google.com");

        Assert.Equal(SmtpResponseCategory.RateLimited, result.Category);
        Assert.NotEqual(SmtpResponseCategory.RecipientRejected, result.Category);
        Assert.Equal(SmtpMailboxStatus.TemporaryFailure, SmtpResponseClassifier.ToMailboxStatus(result.Category));
    }

    [Fact]
    public void Classify_MicrosoftConsumer47x_UsesPolicyCooldownPath()
    {
        var result = _classifier.Classify(
            SmtpCommand.RcptTo, 451, "451 4.7.0 Temporary server policy response", TimeSpan.Zero,
            MailProvider.MicrosoftConsumer, "outlook-com.olc.protection.outlook.com");

        Assert.Equal(SmtpResponseCategory.RateLimited, result.Category);
        Assert.NotEqual(SmtpResponseCategory.RecipientRejected, result.Category);
    }

    [Fact]
    public void Classify_Generic541AccessDenied_RemainsPolicyBlocked()
    {
        var result = _classifier.Classify(
            SmtpCommand.RcptTo, 550, "550 5.4.1 Recipient address rejected: Access denied.", TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.com");

        Assert.Equal(SmtpResponseCategory.VerificationBlocked, result.Category);
    }

    [Fact]
    public void SessionEvidence_RequiresSuccessfulMailFromForStrongRecipientRejection()
    {
        var mailFromRejected = new SmtpSessionEvidence(
            SmtpCommand.MailFrom,
            [new(SmtpCommand.MailFrom, 550, "5.7.1", SmtpResponseCategory.VerificationBlocked,
                SmtpResponseTextClassification.PolicyRejection, TimeSpan.Zero)],
            "mx.example.com", TimeSpan.Zero, "probe@validator.example");
        var recipientRejected = new SmtpSessionEvidence(
            SmtpCommand.RcptTo,
            [
                new(SmtpCommand.MailFrom, 250, "2.1.0", SmtpResponseCategory.Accepted,
                    SmtpResponseTextClassification.Success, TimeSpan.Zero),
                new(SmtpCommand.RcptTo, 550, "5.1.1", SmtpResponseCategory.RecipientRejected,
                    SmtpResponseTextClassification.RecipientDoesNotExist, TimeSpan.Zero)
            ],
            "mx.example.com", TimeSpan.Zero, "probe@validator.example");

        Assert.False(mailFromRejected.RecipientStageReached);
        Assert.False(mailFromRejected.HasStrongRecipientRejection);
        Assert.True(recipientRejected.RecipientStageReached);
        Assert.True(recipientRejected.HasStrongRecipientRejection);
    }
}
