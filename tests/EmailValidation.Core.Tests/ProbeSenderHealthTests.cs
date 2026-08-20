using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class ProbeSenderHealthTests
{
    [Fact]
    public async Task PlaceholderSender_IsRejectedWithoutDnsLookup()
    {
        var dns = new CountingDns();
        using var checker = Checker(["validator@example.com"], dns);

        var result = await checker.CheckAsync();

        Assert.Equal(ProbeSenderHealthStatus.NotConfigured, result.Status);
        Assert.Equal(0, dns.Calls);
    }

    [Fact]
    public async Task OperationalSenderHealth_IsCached()
    {
        var dns = new CountingDns();
        using var checker = Checker(["probe@validator.test"], dns);

        var first = await checker.CheckAsync();
        var second = await checker.CheckAsync();

        Assert.Equal(ProbeSenderHealthStatus.Valid, first.Status);
        Assert.Equal(first, second);
        Assert.Equal(1, dns.Calls);
    }

    [Fact]
    public async Task Pool_UsesStickyBoundedRotation()
    {
        using var checker = Checker(
            ["probe1@validator.test", "probe2@validator.test", "probe3@validator.test"],
            new CountingDns(),
            maxValidations: 10);
        var selected = new List<string>();

        for (var index = 0; index < 25; index++)
            selected.Add((await checker.GetSenderAsync(ProbeSenderContext.Empty))!.Sender);

        Assert.All(selected[..10], sender => Assert.Equal("probe1@validator.test", sender));
        Assert.All(selected[10..20], sender => Assert.Equal("probe2@validator.test", sender));
        Assert.All(selected[20..], sender => Assert.Equal("probe3@validator.test", sender));
        Assert.Equal(2, checker.GetSnapshot().ScheduledRotations);
    }

    [Fact]
    public async Task TimeThreshold_RotatesOnlyANewSession()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var checker = Checker(
            ["probe1@validator.test", "probe2@validator.test"],
            new CountingDns(),
            maxValidations: 100,
            maxActiveMinutes: 1,
            timeProvider: time);

        var existingSession = await checker.GetSenderAsync(ProbeSenderContext.Empty);
        time.Advance(TimeSpan.FromMinutes(2));
        var newSession = await checker.GetSenderAsync(ProbeSenderContext.Empty);

        Assert.Equal("probe1@validator.test", existingSession!.Sender);
        Assert.Equal("probe2@validator.test", newSession!.Sender);
    }

    [Fact]
    public async Task ConcurrentSelections_DoNotRaceOrSubstantiallyExceedThreshold()
    {
        var senders = Enumerable.Range(0, 12).Select(index => $"probe{index}@validator.test").ToArray();
        using var checker = Checker(senders, new CountingDns(), maxValidations: 10);

        var selections = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            checker.GetSenderAsync(ProbeSenderContext.Empty)));

        Assert.DoesNotContain(selections, selection => selection is null);
        Assert.All(selections.Select(selection => selection!.Sender).GroupBy(sender => sender),
            group => Assert.InRange(group.Count(), 1, 10));
    }

    [Fact]
    public async Task SenderSpecificMailFromFailure_RetiresSenderAndSelectsAlternate()
    {
        using var checker = Checker(
            ["probe1@validator.test", "probe2@validator.test"],
            new CountingDns());
        var first = await checker.GetSenderAsync(ProbeSenderContext.Empty);
        var failure = MailFromFailure("550 5.1.0 Sender address rejected", 550);

        await checker.RecordOutcomeAsync(new(
            first!.Sender,
            SmtpSenderFailureClassifier.Classify(failure),
            failure));
        var next = await checker.GetSenderAsync(
            new ProbeSenderContext(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first.Sender }));

        Assert.Equal("probe2@validator.test", next!.Sender);
        Assert.Equal(1, checker.GetSnapshot().SenderRetirements);
        Assert.Equal(1, checker.GetSnapshot().FailureTriggeredRotations);
    }

    [Fact]
    public async Task TemporarySenderFailure_CoolsSenderAndSelectsAlternate()
    {
        using var checker = Checker(
            ["probe1@validator.test", "probe2@validator.test"],
            new CountingDns());
        var first = await checker.GetSenderAsync(ProbeSenderContext.Empty);
        var failure = MailFromFailure("451 4.3.0 Sender temporarily rejected", 451);

        await checker.RecordOutcomeAsync(new(
            first!.Sender,
            SmtpSenderFailureClassifier.Classify(failure),
            failure));
        var next = await checker.GetSenderAsync(
            new ProbeSenderContext(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first.Sender }));

        Assert.Equal("probe2@validator.test", next!.Sender);
        Assert.Equal(1, checker.GetSnapshot().SenderCooldowns);
    }

    [Fact]
    public async Task UnexplainedPermanentMailFromRejection_RetiresSenderAndSelectsAlternate()
    {
        using var checker = Checker(
            ["probe1@validator.test", "probe2@validator.test"],
            new CountingDns());
        var first = await checker.GetSenderAsync(ProbeSenderContext.Empty);
        var failure = MailFromFailure("550 Requested action not taken", 550);

        var outcome = SmtpSenderFailureClassifier.Classify(failure);
        await checker.RecordOutcomeAsync(new(first!.Sender, outcome, failure));
        var next = await checker.GetSenderAsync(
            new ProbeSenderContext(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first.Sender }));

        Assert.Equal(ProbeSenderOutcomeKind.SenderInvalid, outcome);
        Assert.Equal("probe2@validator.test", next!.Sender);
    }

    [Theory]
    [InlineData(SmtpCommand.RcptTo, 550, "550 5.1.1 User unknown")]
    [InlineData(SmtpCommand.MailFrom, 451, "451 4.7.0 Rate limit exceeded")]
    [InlineData(SmtpCommand.MailFrom, 550, "550 5.7.1 Source IP reputation block")]
    [InlineData(SmtpCommand.MailFrom, 550, "550 5.7.1 Rejected by policy")]
    public void RotationPolicy_DoesNotRotateForRecipientOrSourceWideFailures(
        SmtpCommand command,
        int responseCode,
        string response)
    {
        var result = MailFromFailure(response, responseCode, command);

        Assert.False(SmtpSenderFailureClassifier.ShouldTryAlternate(result));
    }

    [Fact]
    public async Task ProviderRestrictions_DoNotReduceSenderSuccessRateOrTriggerRotation()
    {
        using var checker = Checker(
            ["probe1@validator.test", "probe2@validator.test"],
            new CountingDns(),
            maxValidations: 50);
        ProbeSenderSelection? selected = null;
        for (var index = 0; index < 10; index++)
        {
            selected = await checker.GetSenderAsync(ProbeSenderContext.Empty);
            var restriction = MailFromFailure(
                "550 5.7.1 Client host blocked using Spamhaus due to source IP reputation",
                550);
            await checker.RecordOutcomeAsync(new(
                selected!.Sender,
                SmtpSenderFailureClassifier.Classify(restriction),
                restriction));
        }

        var next = await checker.GetSenderAsync(ProbeSenderContext.Empty);

        Assert.Equal("probe1@validator.test", selected!.Sender);
        Assert.Equal("probe1@validator.test", next!.Sender);
        Assert.Equal(0, checker.GetSnapshot().ScheduledRotations);
    }

    [Fact]
    public void SmtpSessionBudget_IsStrictAndScoped()
    {
        var budget = new SmtpSessionBudget();
        using (budget.Begin(2))
        {
            Assert.True(budget.TryConsume());
            Assert.True(budget.TryConsume());
            Assert.False(budget.TryConsume());
        }
        Assert.True(budget.TryConsume());
    }

    private static ProbeSenderHealthChecker Checker(
        string[] senders,
        IDnsMailResolver dns,
        int maxValidations = 50,
        int maxActiveMinutes = 15,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new EmailValidationOptions
        {
            Smtp = new SmtpOptions { ProbeSenderHealthCacheMinutes = 60 },
            ProbeSenderSource = new ProbeSenderSourceOptions
            {
                Index = "senders",
                QueryJson = "{\"match_all\":{}}",
                QueryLimit = Math.Max(10, senders.Length),
                RefreshThreshold = 1,
                RefreshIntervalSeconds = 300
            },
            ProbeSenderRotation = new ProbeSenderRotationOptions
            {
                MaxValidationsPerSender = maxValidations,
                MaxActiveMinutes = maxActiveMinutes,
                JitterPercent = 0,
                SenderCooldownSeconds = 60
            }
        });
        return new(
            new FakeSource(senders),
            new EmailNormalizer(),
            dns,
            new ProbeSenderRotationPolicy(options),
            new FixedJitter(),
            timeProvider ?? TimeProvider.System,
            options,
            NullLogger<ProbeSenderHealthChecker>.Instance);
    }

    private static SmtpProbeResult MailFromFailure(
        string response,
        int responseCode,
        SmtpCommand command = SmtpCommand.MailFrom)
    {
        var category = response.Contains("Rate", StringComparison.OrdinalIgnoreCase)
            ? SmtpResponseCategory.RateLimited
            : responseCode < 500
                ? SmtpResponseCategory.TemporaryFailure
                : command == SmtpCommand.RcptTo
                    ? SmtpResponseCategory.RecipientRejected
                    : SmtpResponseCategory.VerificationBlocked;
        var textClassification = category == SmtpResponseCategory.RateLimited
            ? SmtpResponseTextClassification.RateLimit
            : response.Contains("policy", StringComparison.OrdinalIgnoreCase)
                ? SmtpResponseTextClassification.PolicyRejection
                : SmtpResponseTextClassification.Unknown;
        var evidence = new SmtpEvidence(
            command, responseCode, responseCode < 500 ? "4.3.0" : "5.7.1", category,
            textClassification, 1, MailProvider.GenericSmtp, "mx.test", 1,
            DateTimeOffset.UtcNow, response);
        var session = new SmtpSessionEvidence(
            command,
            [new(command, responseCode, evidence.EnhancedStatusCode, category, textClassification, TimeSpan.Zero)],
            "mx.test", TimeSpan.Zero, "probe1@validator.test");
        return new(SmtpMailboxStatus.Blocked, responseCode, response, TimeSpan.Zero,
            Evidence: evidence, SessionEvidence: session);
    }

    private sealed class FakeSource(IReadOnlyCollection<string> senders) : IProbeSenderSource
    {
        public Task<IReadOnlyCollection<ProbeSenderCandidate>> GetCandidatesAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ProbeSenderCandidate>>(
                senders.Take(limit).Select(sender => new ProbeSenderCandidate(sender, DateTimeOffset.UtcNow)).ToArray());
    }

    private sealed class FixedJitter : IProbeSenderJitter
    {
        public int Apply(int target, int percent) => target;
    }

    private sealed class CountingDns : IDnsMailResolver
    {
        public int Calls { get; private set; }

        public Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new DnsLookupResult(
                DnsStatus.Success, true, [new MxRecord(10, $"mx.{domain}")], false, TimeSpan.Zero));
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
