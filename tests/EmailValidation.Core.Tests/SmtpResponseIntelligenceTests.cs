using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class SmtpResponseIntelligenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ReplayFixture_ProducesExpectedNormalizedReasonsAndPolicyDecisions()
    {
        var options = DefaultOptions();
        var classifier = new SmtpResponseClassifier(options);
        var policy = new SmtpResponseDecisionPolicy(options);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "smtp-response-intelligence-v1.json");
        var fixtures = JsonSerializer.Deserialize<ReplayFixture[]>(File.ReadAllText(path), JsonOptions)!;

        Assert.NotEmpty(fixtures);
        foreach (var fixture in fixtures)
        {
            var classification = classifier.Classify(new(
                Parse<SmtpCommand>(fixture.Stage), fixture.Code,
                fixture.RepeatCount is > 1 ? string.Concat(Enumerable.Repeat(fixture.Response, fixture.RepeatCount.Value)) : fixture.Response,
                TimeSpan.Zero,
                Parse<MailProvider>(fixture.Provider), "mx.fixture.example.test"));
            var decision = policy.Decide(classification);

            Assert.True(Parse<SmtpNormalizedReason>(fixture.Reason) == classification.Reason,
                $"Fixture '{fixture.Provider}/{fixture.Stage}/{fixture.Response[..Math.Min(60, fixture.Response.Length)]}' expected {fixture.Reason} but got {classification.Reason}; sanitized='{classification.SanitizedResponse}', fingerprint='{classification.ResponseFingerprint}'.");
            Assert.Equal(Parse<SmtpMailboxImpact>(fixture.MailboxImpact), decision.MailboxImpact);
            Assert.Equal(Parse<SmtpRetryDisposition>(fixture.Retry), decision.RetryDisposition);
            Assert.Equal(Parse<SmtpCooldownScope>(fixture.Cooldown), decision.CooldownScope);
            Assert.Equal(Parse<SmtpResponseCategory>(fixture.Category), decision.CanonicalCategory);
            Assert.Equal(fixture.SenderRotation, decision.AllowSenderRotation);
            Assert.False(string.IsNullOrWhiteSpace(classification.ResponseFingerprint));
            if (fixture.ExpectedFingerprint is not null)
                Assert.Equal(fixture.ExpectedFingerprint, classification.ResponseFingerprint);
        }
    }

    [Fact]
    public void DefaultRolloutMode_IsShadow()
    {
        Assert.Equal(SmtpResponseIntelligenceMode.Shadow,
            new EmailValidationOptions().SmtpResponseIntelligence.Mode);
    }

    [Fact]
    public void ShadowMode_RecordsDisagreementWithoutChangingCanonicalOutcome()
    {
        var options = DefaultOptions(SmtpResponseIntelligenceMode.Shadow);
        var metrics = new SmtpResponseIntelligenceMetrics();
        var orchestrator = Orchestrator(options, metrics);

        var evidence = orchestrator.Classify(SmtpCommand.RcptTo, 550,
            "550 Requested action not taken", TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.test");

        Assert.Equal(SmtpResponseCategory.VerificationBlocked, evidence.Category);
        Assert.Equal(SmtpNormalizedReason.UnknownProviderResponse, evidence.Intelligence?.Reason);
        Assert.Equal(SmtpResponseCategory.Unknown, evidence.Decision?.CanonicalCategory);
        Assert.Equal(SmtpResponseIntelligenceMode.Shadow, evidence.IntelligenceMode);
        Assert.False(evidence.CanonicalOutcomeChanged);
        Assert.Equal(1, metrics.GetSnapshot().Disagreements);
    }

    [Fact]
    public void EnforcedMode_AppliesCandidatePolicyAndMarksChangedOutcome()
    {
        var options = DefaultOptions(SmtpResponseIntelligenceMode.Enforced);
        var metrics = new SmtpResponseIntelligenceMetrics();
        var orchestrator = Orchestrator(options, metrics);

        var evidence = orchestrator.Classify(SmtpCommand.RcptTo, 550,
            "550 Requested action not taken", TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.test");

        Assert.Equal(SmtpResponseCategory.Unknown, evidence.Category);
        Assert.Equal(SmtpResponseIntelligenceMode.Enforced, evidence.IntelligenceMode);
        Assert.True(evidence.CanonicalOutcomeChanged);
    }

    [Fact]
    public void DisabledMode_DoesNotInvokeCandidate()
    {
        var options = DefaultOptions(SmtpResponseIntelligenceMode.Disabled);
        var metrics = new SmtpResponseIntelligenceMetrics();
        var orchestrator = new SmtpResponseClassificationOrchestrator(
            new CanonicalSmtpResponseClassifierAdapter(), new ThrowingCandidate(),
            new SmtpResponseDecisionPolicy(options), metrics, options);

        var evidence = orchestrator.Classify(SmtpCommand.RcptTo, 550, "550 5.1.1 User unknown",
            TimeSpan.Zero, MailProvider.GenericSmtp, "mx.example.test");

        Assert.Equal(SmtpResponseCategory.RecipientRejected, evidence.Category);
        Assert.Null(evidence.Intelligence);
        Assert.Equal(0, metrics.GetSnapshot().CandidateFailures);
    }

    [Theory]
    [InlineData(SmtpResponseIntelligenceMode.Shadow, SmtpResponseCategory.VerificationBlocked)]
    [InlineData(SmtpResponseIntelligenceMode.Enforced, SmtpResponseCategory.Unknown)]
    public void CandidateFailure_UsesModeSafeFallback(
        SmtpResponseIntelligenceMode mode,
        SmtpResponseCategory expected)
    {
        var options = DefaultOptions(mode);
        var metrics = new SmtpResponseIntelligenceMetrics();
        var orchestrator = new SmtpResponseClassificationOrchestrator(
            new CanonicalSmtpResponseClassifierAdapter(), new ThrowingCandidate(),
            new SmtpResponseDecisionPolicy(options), metrics, options);

        var evidence = orchestrator.Classify(SmtpCommand.RcptTo, 550,
            "550 Requested action not taken", TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.test");

        Assert.Equal(expected, evidence.Category);
        Assert.Equal(1, metrics.GetSnapshot().CandidateFailures);
        Assert.NotEqual(SmtpResponseCategory.RecipientRejected, evidence.Category);
    }

    [Fact]
    public void MailFromRejection_NeverInvalidatesRecipientAndOnlyRotatesOutboundIdentity()
    {
        var options = DefaultOptions();
        var classifier = new SmtpResponseClassifier(options);
        var policy = new SmtpResponseDecisionPolicy(options);
        var classification = classifier.Classify(new(
            SmtpCommand.MailFrom, 550, "550 5.1.1 Sender invalid", TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.test"));

        var decision = policy.Decide(classification);

        Assert.Equal(SmtpMailboxImpact.None, decision.MailboxImpact);
        Assert.True(decision.AllowSenderRotation);
        Assert.Equal(SmtpCooldownScope.OutboundIdentity, decision.CooldownScope);
        Assert.NotEqual(SmtpResponseCategory.RecipientRejected, decision.CanonicalCategory);
    }

    [Fact]
    public void RecipientSpecificReason_AtWrongStageCannotInvalidateMailbox()
    {
        var options = DefaultOptions();
        var policy = new SmtpResponseDecisionPolicy(options);
        var classification = new SmtpResponseIntelligence(
            SmtpCommand.MailFrom, 550, 5, "5.1.1", SmtpNormalizedReason.MailboxNotFound,
            SmtpEvidenceStrength.High, MailProvider.GenericSmtp, "rules-1",
            "generic-mailbox-not-found", null);

        Assert.NotEqual(SmtpMailboxImpact.Invalid, policy.Decide(classification).MailboxImpact);
    }

    [Theory]
    [InlineData(SmtpCommand.Greeting)]
    [InlineData(SmtpCommand.Ehlo)]
    [InlineData(SmtpCommand.MailFrom)]
    public void MailboxText_OutsideRcptStageCannotBecomeMailboxNotFound(SmtpCommand stage)
    {
        var classifier = new SmtpResponseClassifier(DefaultOptions());

        var classification = classifier.Classify(new(stage, 550,
            "550 5.1.1 User unknown", TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.test"));

        Assert.NotEqual(SmtpNormalizedReason.MailboxNotFound, classification.Reason);
    }

    [Fact]
    public void StructuredMailboxStatus_PrecedesBroadPolicyText()
    {
        var classifier = new SmtpResponseClassifier(DefaultOptions());

        var classification = classifier.Classify(new(SmtpCommand.RcptTo, 550,
            "550 5.1.1 User unknown; access denied by policy", TimeSpan.Zero,
            MailProvider.GenericSmtp, "mx.example.test"));

        Assert.Equal(SmtpNormalizedReason.MailboxNotFound, classification.Reason);
        Assert.Equal("generic-mailbox-not-found", classification.ResponseFingerprint);
    }

    [Fact]
    public void AvailableObservationContext_IsRetainedWithoutInventingMissingIdentity()
    {
        var classifier = new SmtpResponseClassifier(DefaultOptions());
        var observedAt = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var observation = new SmtpResponseObservationContext(
            ValidationId: "validation-fixture", RecipientDomain: "example.test",
            SenderIdentityId: "sender-hash", ObservedAtUtc: observedAt, StrategyVersion: "strategy-1");

        var classification = classifier.Classify(new(SmtpCommand.RcptTo, 250,
            "250 2.1.5 OK", TimeSpan.Zero, MailProvider.GenericSmtp,
            "mx.example.test", Observation: observation));

        Assert.Equal("validation-fixture", classification.ValidationId);
        Assert.Equal("example.test", classification.RecipientDomain);
        Assert.Equal("sender-hash", classification.SenderIdentityId);
        Assert.Equal(observedAt, classification.ObservedAtUtc);
        Assert.Equal("strategy-1", classification.StrategyVersion);
        Assert.Null(classification.OutboundIdentityId);
    }

    [Fact]
    public void Fingerprint_IsStableAndSanitizedAcrossAddressesIpsAndTimestamps()
    {
        var options = DefaultOptions();
        var classifier = new SmtpResponseClassifier(options);
        var first = classifier.Classify(new(SmtpCommand.RcptTo, 451,
            "451 4.7.23 Sending IP 192.0.2.10 rejected user-one@example.test at 2026-08-26T10:11:12Z",
            TimeSpan.Zero, MailProvider.GoogleWorkspace, "mx.example.test"));
        var second = classifier.Classify(new(SmtpCommand.RcptTo, 451,
            "451 4.7.23 Sending IP 198.51.100.20 rejected user-two@example.test at 2026-08-27T11:12:13Z",
            TimeSpan.Zero, MailProvider.GoogleWorkspace, "mx.example.test"));

        Assert.Equal("google-ip-policy-block", first.ResponseFingerprint);
        Assert.Equal(first.ResponseFingerprint, second.ResponseFingerprint);
        Assert.DoesNotContain("192.0.2.10", first.SanitizedResponse, StringComparison.Ordinal);
        Assert.DoesNotContain("user-one@example.test", first.SanitizedResponse, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-08-26", first.SanitizedResponse, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponseProcessing_IsBoundedBeforeRegexEvaluation()
    {
        var settings = new EmailValidationOptions();
        settings.SmtpResponseIntelligence.MaximumResponseCharacters = 256;
        settings.SmtpResponseIntelligence.RegexTimeoutMilliseconds = 10;
        var classifier = new SmtpResponseClassifier(Options.Create(settings));

        var result = classifier.Classify(new(SmtpCommand.RcptTo, 550,
            new string('a', 100_000), TimeSpan.Zero, MailProvider.GenericSmtp, "mx.example.test"));

        Assert.NotNull(result.SanitizedResponse);
        Assert.True(result.SanitizedResponse!.Length <= 256);
        Assert.Equal(SmtpNormalizedReason.UnknownProviderResponse, result.Reason);
    }

    private static SmtpResponseClassificationOrchestrator Orchestrator(
        IOptions<EmailValidationOptions> options,
        ISmtpResponseIntelligenceMetrics metrics) => new(
            new CanonicalSmtpResponseClassifierAdapter(),
            new SmtpResponseClassifier(options),
            new SmtpResponseDecisionPolicy(options),
            metrics,
            options);

    private static IOptions<EmailValidationOptions> DefaultOptions(
        SmtpResponseIntelligenceMode mode = SmtpResponseIntelligenceMode.Shadow)
    {
        var settings = new EmailValidationOptions();
        settings.SmtpResponseIntelligence.Mode = mode;
        return Options.Create(settings);
    }

    private static T Parse<T>(string value) where T : struct, Enum => Enum.Parse<T>(value, true);

    private sealed record ReplayFixture(
        string Stage,
        int? Code,
        string Response,
        string Provider,
        string Reason,
        string MailboxImpact,
        string Retry,
        string Cooldown,
        string Category,
        string? ExpectedFingerprint = null,
        int? RepeatCount = null,
        bool SenderRotation = false);

    private sealed class ThrowingCandidate : ISmtpResponseIntelligenceClassifier
    {
        public SmtpResponseIntelligence Classify(SmtpResponseClassificationContext context) =>
            throw new InvalidOperationException("candidate fixture failure");
    }
}
