using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class ProbeSenderAffinityTests
{
    [Fact]
    public void Affinity_IsDomainScoped_AndGlobalInvalidationRemovesEveryReference()
    {
        var store = Store();
        store.SetAffinity("example.com", "probe-a@sender.test");
        store.SetAffinity("another.com", "probe-a@sender.test");
        store.SetAffinity("third.com", "probe-b@sender.test");

        store.Remove("example.com");

        Assert.Null(store.GetAffinity("example.com"));
        Assert.Equal("probe-a@sender.test", store.GetAffinity("another.com")?.Sender);
        store.RemoveSender("probe-a@sender.test");
        Assert.Null(store.GetAffinity("another.com"));
        Assert.Equal("probe-b@sender.test", store.GetAffinity("third.com")?.Sender);
    }

    [Fact]
    public void DomainCompatibility_DoesNotExcludeSenderForOtherDomains()
    {
        var store = Store();

        store.MarkIncompatible("example.com", "probe-a@sender.test");

        Assert.Contains("probe-a@sender.test", store.GetIncompatibleSenders("example.com"));
        Assert.Empty(store.GetIncompatibleSenders("another.com"));
    }

    [Fact]
    public void RecipientAndProviderFailures_AreNotSenderSpecific()
    {
        var recipient = Result(
            SmtpCommand.RcptTo, SmtpResponseCategory.RecipientRejected,
            SmtpResponseTextClassification.RecipientDoesNotExist, 550, "550 user unknown", mailFromAccepted: true);
        var provider = Result(
            SmtpCommand.MailFrom, SmtpResponseCategory.RateLimited,
            SmtpResponseTextClassification.RateLimit, 451, "451 rate limited", mailFromAccepted: false);

        Assert.Equal(ProbeSenderOutcomeKind.RecipientOutcome, SmtpSenderFailureClassifier.Classify(recipient));
        Assert.False(SmtpSenderFailureClassifier.ShouldTryAlternate(recipient));
        Assert.Equal(ProbeSenderOutcomeKind.ProviderRestriction, SmtpSenderFailureClassifier.Classify(provider));
        Assert.False(SmtpSenderFailureClassifier.ShouldTryAlternate(provider));
    }

    private static ProbeSenderAffinityStore Store()
    {
        var options = Options.Create(new EmailValidationOptions
        {
            ProbeSenderRotation = new ProbeSenderRotationOptions
            {
                SenderAffinityMinutes = 60,
                SenderCompatibilityMinutes = 60
            }
        });
        return new(TimeProvider.System, options);
    }

    private static SmtpProbeResult Result(
        SmtpCommand failedCommand,
        SmtpResponseCategory category,
        SmtpResponseTextClassification text,
        int code,
        string response,
        bool mailFromAccepted)
    {
        var stages = new List<SmtpStageResult>();
        if (mailFromAccepted)
            stages.Add(new(SmtpCommand.MailFrom, 250, "2.1.0", SmtpResponseCategory.Accepted,
                SmtpResponseTextClassification.Success, TimeSpan.Zero));
        stages.Add(new(failedCommand, code, null, category, text, TimeSpan.Zero, response));
        var evidence = new SmtpEvidence(
            failedCommand, code, null, category, text, 1, MailProvider.Microsoft365,
            "mx.test", 1, DateTimeOffset.UtcNow, response);
        var session = new SmtpSessionEvidence(
            failedCommand, stages, "mx.test", TimeSpan.Zero, "probe-a@sender.test");
        return new(SmtpMailboxStatus.Blocked, code, response, TimeSpan.Zero,
            Evidence: evidence, SessionEvidence: session);
    }
}
