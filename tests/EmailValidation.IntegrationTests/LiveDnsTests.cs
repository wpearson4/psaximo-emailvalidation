using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.IntegrationTests;

public sealed class LiveDnsTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task GmailMxResolution_WorksWhenLiveTestsAreEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("EMAIL_VALIDATION_RUN_LIVE_TESTS"), "1", StringComparison.Ordinal))
            return;

        var resolver = new MxDnsResolver(
            Microsoft.Extensions.Options.Options.Create(new EmailValidationOptions()),
            NullLogger<MxDnsResolver>.Instance);

        var result = await resolver.ResolveAsync("gmail.com", CancellationToken.None);

        Assert.Equal(DnsStatus.Success, result.Status);
        Assert.NotEmpty(result.MxRecords);
    }
}
