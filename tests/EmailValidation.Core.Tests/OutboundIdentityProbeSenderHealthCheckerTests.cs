using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class OutboundIdentityProbeSenderHealthCheckerTests
{
    [Fact]
    public async Task SharedConfiguredSenderDomain_IsCheckedOnce()
    {
        var dns = new RecordingDns(new(
            DnsStatus.Success,
            true,
            [new MxRecord(10, "email.digitalwarehouse.io")],
            false,
            TimeSpan.Zero));
        var checker = new OutboundIdentityProbeSenderHealthChecker(
            new EmailNormalizer(), dns, Options.Create(OptionsWithIdentities()));

        var result = await checker.CheckAsync();

        Assert.True(result.IsOperational);
        Assert.Equal("validation.email.digitalwarehouse.io", result.Domain);
        Assert.Equal(["validation.email.digitalwarehouse.io"], dns.Domains);
    }

    [Fact]
    public async Task MissingSenderDomainMx_DisablesLiveSmtp()
    {
        var dns = new RecordingDns(new(
            DnsStatus.Success, true, [], false, TimeSpan.Zero));
        var checker = new OutboundIdentityProbeSenderHealthChecker(
            new EmailNormalizer(), dns, Options.Create(OptionsWithIdentities()));

        var result = await checker.CheckAsync();

        Assert.Equal(ProbeSenderHealthStatus.NoMailRouting, result.Status);
        Assert.False(result.IsOperational);
    }

    private static EmailValidationOptions OptionsWithIdentities() => new()
    {
        OutboundIdentities = new()
        {
            Enabled = true,
            Identities =
            [
                new()
                {
                    IdentityId = "smtp-162",
                    ProbeSenderAddress = "probe-162@validation.email.digitalwarehouse.io"
                },
                new()
                {
                    IdentityId = "smtp-163",
                    ProbeSenderAddress = "probe-163@validation.email.digitalwarehouse.io"
                }
            ]
        }
    };

    private sealed class RecordingDns(DnsLookupResult result) : IDnsMailResolver
    {
        public List<string> Domains { get; } = [];

        public Task<DnsLookupResult> ResolveAsync(
            string domain,
            CancellationToken cancellationToken = default)
        {
            Domains.Add(domain);
            return Task.FromResult(result);
        }
    }
}
