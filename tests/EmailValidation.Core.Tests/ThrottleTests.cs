using EmailValidation.Core;
using EmailValidation.Infrastructure;

namespace EmailValidation.Core.Tests;

public sealed class ThrottleTests
{
    [Fact]
    public async Task GlobalLimitHoldsSecondLeaseUntilFirstIsReleased()
    {
        var settings = new EmailValidationOptions
        {
            Smtp = new SmtpOptions
            {
                GlobalConcurrency = 1,
                PerDomainConcurrency = 1,
                DelayBetweenDomainRequestsMilliseconds = 0
            }
        };
        using var throttle = new DomainSmtpProbeThrottle(Microsoft.Extensions.Options.Options.Create(settings));
        var context = new SmtpThrottleContext("example.com", "mx.example.com");
        var first = await throttle.AcquireAsync(context);

        var secondTask = throttle.AcquireAsync(context).AsTask();
        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
        await second.DisposeAsync();
    }

    [Fact]
    public async Task ProviderLimitAppliesAcrossDifferentDomains()
    {
        var settings = new EmailValidationOptions
        {
            Smtp = new SmtpOptions
            {
                GlobalConcurrency = 2,
                PerDomainConcurrency = 1,
                PerProviderConcurrency = 1,
                DelayBetweenDomainRequestsMilliseconds = 0
            }
        };
        using var throttle = new DomainSmtpProbeThrottle(Microsoft.Extensions.Options.Options.Create(settings));
        var first = await throttle.AcquireAsync(new SmtpThrottleContext(
            "one.example", "mx.one.example", MailProvider.GoogleWorkspace));

        var secondTask = throttle.AcquireAsync(new SmtpThrottleContext(
            "two.example", "mx.two.example", MailProvider.GoogleWorkspace)).AsTask();
        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
        await second.DisposeAsync();
    }
}
