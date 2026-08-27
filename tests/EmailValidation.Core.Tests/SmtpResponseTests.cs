using EmailValidation.Core;
using EmailValidation.Infrastructure;

namespace EmailValidation.Core.Tests;

public sealed class SmtpResponseTests
{
    [Theory]
    [InlineData(250, "2.1.5 recipient ok", SmtpMailboxStatus.Accepted)]
    [InlineData(251, "will forward", SmtpMailboxStatus.Accepted)]
    [InlineData(252, "cannot verify", SmtpMailboxStatus.Unknown)]
    [InlineData(421, "try again later", SmtpMailboxStatus.TemporaryFailure)]
    [InlineData(450, "greylisted", SmtpMailboxStatus.TemporaryFailure)]
    [InlineData(550, "user unknown", SmtpMailboxStatus.Rejected)]
    [InlineData(554, "access denied by policy", SmtpMailboxStatus.Blocked)]
    public void Categorize_DistinguishesTransientPermanentAndBlocked(int code, string text, SmtpMailboxStatus expected)
    {
        var evidence = new SmtpResponseClassifier().Classify(
            SmtpCommand.RcptTo, code, text, TimeSpan.Zero, MailProvider.Unknown, "mx.example.test");
        Assert.Equal(expected, SmtpResponseClassifier.ToProbeResult(evidence, TimeSpan.Zero).Status);
    }
}
