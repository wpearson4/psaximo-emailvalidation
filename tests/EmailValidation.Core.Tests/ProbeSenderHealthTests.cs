using EmailValidation.Core;
using EmailValidation.Infrastructure;

namespace EmailValidation.Core.Tests;

public sealed class ProbeSenderHealthTests
{
    [Fact]
    public async Task PlaceholderSender_IsRejectedWithoutDnsLookup()
    {
        var dns = new CountingDns();
        var checker = Checker("validator@example.com", dns);

        var result = await checker.CheckAsync();

        Assert.Equal(ProbeSenderHealthStatus.NoMailRouting, result.Status);
        Assert.Equal(0, dns.Calls);
    }

    [Fact]
    public async Task OperationalSenderHealth_IsCached()
    {
        var dns = new CountingDns();
        var checker = Checker("probe@validator.example", dns);

        var first = await checker.CheckAsync();
        var second = await checker.CheckAsync();

        Assert.Equal(ProbeSenderHealthStatus.Valid, first.Status);
        Assert.Equal(first, second);
        Assert.Equal(1, dns.Calls);
    }

    [Fact]
    public async Task Pool_RotatesRoundRobinAcrossHealthyEnabledSenders()
    {
        var checker = Checker(
            [new() { Address = "probe1@validator.test" }, new() { Address = "probe2@validator.test" }],
            new CountingDns());

        var first = await checker.SelectAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var second = await checker.SelectAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("probe1@validator.test", first?.Sender);
        Assert.Equal("probe2@validator.test", second?.Sender);
    }

    [Fact]
    public async Task SenderSpecificMailFromFailure_CoolsSenderAndSelectsAlternate()
    {
        var checker = Checker(
            [new() { Address = "probe1@validator.test" }, new() { Address = "probe2@validator.test" }],
            new CountingDns());
        var first = await checker.SelectAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.NotNull(first?.Sender);

        checker.ReportResult(first.Sender, MailFromFailure("550 5.7.1 Sender rejected"));
        var next = await checker.SelectAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("probe2@validator.test", next?.Sender);
    }

    [Theory]
    [InlineData(SmtpCommand.RcptTo, "550 5.1.1 User unknown")]
    [InlineData(SmtpCommand.MailFrom, "451 4.7.0 Rate limit exceeded")]
    [InlineData(SmtpCommand.MailFrom, "550 5.7.1 Source IP reputation block")]
    public void RotationPolicy_DoesNotRotateForRecipientOrSourceWideFailures(
        SmtpCommand command,
        string response)
    {
        var result = MailFromFailure(response, command);

        Assert.False(SmtpSenderRotationPolicy.IsSenderSpecificFailure(result));
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

    private static ProbeSenderHealthChecker Checker(string sender, IDnsMailResolver dns) => new(
        new EmailNormalizer(),
        dns,
        Microsoft.Extensions.Options.Options.Create(new EmailValidationOptions
        {
            Smtp = new SmtpOptions { ProbeSender = sender, ProbeSenderHealthCacheMinutes = 60 }
        }));

    private static ProbeSenderHealthChecker Checker(
        List<ProbeSenderOptions> senders,
        IDnsMailResolver dns) => new(
        new EmailNormalizer(),
        dns,
        Microsoft.Extensions.Options.Options.Create(new EmailValidationOptions
        {
            Smtp = new SmtpOptions
            {
                ProbeSenders = senders,
                ProbeSenderHealthCacheMinutes = 60,
                SenderCooldownSeconds = 60
            }
        }));

    private static SmtpProbeResult MailFromFailure(
        string response,
        SmtpCommand command = SmtpCommand.MailFrom)
    {
        var textClassification = response.Contains("Rate", StringComparison.OrdinalIgnoreCase)
            ? SmtpResponseTextClassification.RateLimit
            : SmtpResponseTextClassification.PolicyRejection;
        var category = textClassification == SmtpResponseTextClassification.RateLimit
            ? SmtpResponseCategory.RateLimited
            : command == SmtpCommand.RcptTo
                ? SmtpResponseCategory.RecipientRejected
                : SmtpResponseCategory.VerificationBlocked;
        var evidence = new SmtpEvidence(
            command, 550, "5.7.1", category, textClassification, 1,
            MailProvider.GenericSmtp, "mx.test", 1, DateTimeOffset.UtcNow, response);
        var session = new SmtpSessionEvidence(
            command, [new(command, 550, "5.7.1", category, textClassification, TimeSpan.Zero)],
            "mx.test", TimeSpan.Zero, "probe1@validator.test");
        return new(SmtpMailboxStatus.Blocked, 550, response, TimeSpan.Zero, Evidence: evidence, SessionEvidence: session);
    }

    private sealed class CountingDns : IDnsMailResolver
    {
        public int Calls { get; private set; }

        public Task<DnsLookupResult> ResolveAsync(
            string domain,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new DnsLookupResult(
                DnsStatus.Success, true, [new MxRecord(10, $"mx.{domain}")], false, TimeSpan.Zero));
        }
    }
}
