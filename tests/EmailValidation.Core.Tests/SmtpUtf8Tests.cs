using EmailValidation.Core;
using EmailValidation.Infrastructure;

namespace EmailValidation.Core.Tests;

public sealed class SmtpUtf8Tests
{
    [Theory]
    [InlineData("250-mx.example | 250-SIZE 10485760 | 250 SMTPUTF8", true)]
    [InlineData("250-mx.example | 250-STARTTLS | 250 SIZE 10485760", false)]
    [InlineData("250-mx.example | 250-smtputf8 | 250 OK", true)]
    public void EhloCapabilityParsing_DetectsSmtpUtf8Token(string response, bool expected)
    {
        Assert.Equal(expected, SmtpMailboxProbe.HasEhloCapability(response, "SMTPUTF8"));
    }

    [Fact]
    public void SmtpUtf8Unsupported_IsExplicitBlockedEvidence_NotMailboxRejection()
    {
        Assert.Equal(SmtpMailboxStatus.Blocked,
            SmtpResponseClassifier.ToMailboxStatus(SmtpResponseCategory.SmtpUtf8Unsupported));
        Assert.NotEqual(SmtpMailboxStatus.Rejected,
            SmtpResponseClassifier.ToMailboxStatus(SmtpResponseCategory.SmtpUtf8Unsupported));
    }

    [Fact]
    public void InternationalRecipient_AddsSmtpUtf8MailFromParameter()
    {
        Assert.Equal("MAIL FROM:<probe@example.com> SMTPUTF8",
            SmtpMailboxProbe.MailFromCommand("probe@example.com", requiresSmtpUtf8: true));
        Assert.Equal("MAIL FROM:<probe@example.com>",
            SmtpMailboxProbe.MailFromCommand("probe@example.com", requiresSmtpUtf8: false));
    }
}
