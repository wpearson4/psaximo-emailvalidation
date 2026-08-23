using EmailValidation.Core;

namespace EmailValidation.Core.Tests;

public sealed class ClassificationEngineTests
{
    private readonly EmailClassificationEngine _classifier = new();

    [Fact]
    public void AcceptedMailboxOnNonCatchAllDomain_IsValid()
    {
        var result = _classifier.Classify(Checks(SmtpMailboxStatus.Accepted, CatchAllStatus.NotCatchAll), DnsStatus.Success);

        Assert.Equal(EmailValidationStatus.Valid, result.Status);
        Assert.InRange(result.Confidence, 0.90, 1.0);
        Assert.Contains(ReasonCode.MailboxAccepted, result.ReasonCodes);
    }

    [Fact]
    public void AcceptedMailboxWithUnknownCatchAll_IsLikelyValid()
    {
        var result = _classifier.Classify(Checks(SmtpMailboxStatus.Accepted, CatchAllStatus.Unknown), DnsStatus.Success);

        Assert.Equal(EmailValidationStatus.LikelyValid, result.Status);
        Assert.Contains(ReasonCode.CatchAllUnknown, result.ReasonCodes);
    }

    [Theory]
    [InlineData(SmtpMailboxStatus.Rejected, ReasonCode.MailboxRejected)]
    public void DefinitiveMailboxFailure_IsInvalid(SmtpMailboxStatus mailbox, ReasonCode reason)
    {
        var result = _classifier.Classify(Checks(mailbox, CatchAllStatus.NotCatchAll), DnsStatus.Success);

        Assert.Equal(EmailValidationStatus.Invalid, result.Status);
        Assert.Contains(reason, result.ReasonCodes);
    }

    [Theory]
    [InlineData(SmtpMailboxStatus.Timeout, ReasonCode.SmtpTimeout)]
    [InlineData(SmtpMailboxStatus.TemporaryFailure, ReasonCode.TemporarySmtpFailure)]
    [InlineData(SmtpMailboxStatus.Blocked, ReasonCode.ProviderBlockedVerification)]
    [InlineData(SmtpMailboxStatus.ConnectionFailure, ReasonCode.SmtpConnectionFailure)]
    public void AmbiguousSmtpFailure_IsUnknown(SmtpMailboxStatus mailbox, ReasonCode reason)
    {
        var result = _classifier.Classify(Checks(mailbox, CatchAllStatus.Unknown), DnsStatus.Success);

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(reason, result.ReasonCodes);
    }

    [Fact]
    public void DisposableOrRoleAccount_RemainsTechnicallyValidWithSeparateRiskReasons()
    {
        var checks = Checks(SmtpMailboxStatus.Accepted, CatchAllStatus.NotCatchAll) with
        {
            DisposableDomain = true,
            RoleAccount = true
        };
        var result = _classifier.Classify(checks, DnsStatus.Success);

        Assert.Equal(EmailValidationStatus.Valid, result.Status);
        Assert.Contains(ReasonCode.DisposableDomain, result.ReasonCodes);
        Assert.Contains(ReasonCode.RoleAccount, result.ReasonCodes);
        Assert.InRange(result.Confidence, 0, 1);
    }

    [Fact]
    public void MissingMx_IsInvalid()
    {
        var result = _classifier.Classify(Checks() with { MxPresent = false }, DnsStatus.Success);

        Assert.Equal(EmailValidationStatus.Invalid, result.Status);
        Assert.Contains(ReasonCode.NoMailExchanger, result.ReasonCodes);
    }

    [Theory]
    [InlineData(DnsStatus.Timeout, ReasonCode.DnsTimeout)]
    [InlineData(DnsStatus.Failure, ReasonCode.DnsFailure)]
    public void TemporaryDnsFailure_IsUnknown(DnsStatus dns, ReasonCode reason)
    {
        var result = _classifier.Classify(Checks(), dns);

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(reason, result.ReasonCodes);
    }

    private static EmailValidationChecks Checks(
        SmtpMailboxStatus mailbox = SmtpMailboxStatus.NotAttempted,
        CatchAllStatus catchAll = CatchAllStatus.NotAttempted) => new()
        {
            SyntaxValid = true,
            DomainExists = true,
            MxPresent = true,
            Mailbox = mailbox,
            CatchAll = catchAll
        };
}
