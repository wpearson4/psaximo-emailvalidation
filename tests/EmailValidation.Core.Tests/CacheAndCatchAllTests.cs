using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class CacheAndCatchAllTests
{
    [Fact]
    public void Cache_ReturnsStoredDomainAndExpiresIt()
    {
        var cache = new InMemoryDomainValidationCache();
        var data = DomainData();
        cache.Store(data, TimeSpan.FromMinutes(1));

        Assert.True(cache.TryGet("EXAMPLE.COM", out var cached));
        Assert.Same(data, cached);

        cache.Store(data, TimeSpan.FromTicks(-1));
        Assert.False(cache.TryGet("example.com", out _));
    }

    [Fact]
    public async Task CatchAll_OneRandomRecipientAccepted_IsUnknown()
    {
        var detector = Detector(SmtpMailboxStatus.Accepted);

        var result = await detector.DetectAsync("example.com", "mx.example.com", MailProvider.Unknown);

        Assert.Equal(CatchAllStatus.Unknown, result.Status);
        Assert.Equal(1, result.Probes);
        Assert.Equal(1, result.Accepted);
    }

    [Fact]
    public async Task CatchAll_TwoRandomRecipientsAccepted_IsLikelyCatchAll()
    {
        var detector = Detector(SmtpMailboxStatus.Accepted, probeCount: 2);

        var result = await detector.DetectAsync("example.com", "mx.example.com", MailProvider.Unknown);

        Assert.Equal(CatchAllStatus.LikelyCatchAll, result.Status);
        Assert.Equal(2, result.Accepted);
    }

    [Fact]
    public async Task CatchAll_AdaptivePlanner_AddsSecondProbeWhenFirstAcceptanceIsInsufficient()
    {
        var options = new EmailValidationOptions
        {
            CatchAll = new CatchAllOptions
            {
                ProbeCount = 1,
                MaxProbeCount = 3,
                MinimumAcceptedProbes = 2
            }
        };
        var detector = new CatchAllDetector(
            new FakeProbe(SmtpMailboxStatus.Accepted),
            Microsoft.Extensions.Options.Options.Create(options));

        var result = await detector.DetectAsync(
            "example.com", "mx.example.com", MailProvider.GenericSmtp);

        Assert.Equal(CatchAllStatus.LikelyCatchAll, result.Status);
        Assert.Equal(2, result.Probes);
        Assert.Equal(2, result.ProbeResults.Count);
    }

    [Fact]
    public async Task CatchAll_GoogleAcceptanceRemainsUnknown()
    {
        var detector = Detector(SmtpMailboxStatus.Accepted, probeCount: 2);

        var result = await detector.DetectAsync("example.com", "mx.example.com", MailProvider.GoogleWorkspace);

        Assert.Equal(CatchAllStatus.Unknown, result.Status);
        Assert.Contains("Google Workspace", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatchAll_MicrosoftRandomAcceptance_IsLikelyGatewayOrCatchAllBehavior()
    {
        var detector = Detector(SmtpMailboxStatus.Accepted);

        var result = await detector.DetectAsync(
            "example.com", "tenant.mail.protection.outlook.com", MailProvider.Microsoft365);

        Assert.Equal(CatchAllStatus.LikelyCatchAll, result.Status);
        Assert.InRange(result.Confidence, 0.70, 0.80);
        Assert.Contains("not mailbox existence", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatchAll_AllRandomRecipientsRejected_IsNotCatchAll()
    {
        var detector = Detector(SmtpMailboxStatus.Rejected);

        var result = await detector.DetectAsync("example.com", "mx.example.com", MailProvider.Unknown);

        Assert.Equal(CatchAllStatus.LikelyNotCatchAll, result.Status);
        Assert.InRange(result.Confidence, 0.80, 0.90);
    }

    [Fact]
    public async Task CatchAll_AmbiguousResponse_IsUnknown()
    {
        var detector = Detector(SmtpMailboxStatus.TemporaryFailure);

        var result = await detector.DetectAsync("example.com", "mx.example.com", MailProvider.Unknown);

        Assert.Equal(CatchAllStatus.Unknown, result.Status);
    }

    private static CatchAllDetector Detector(SmtpMailboxStatus status, int probeCount = 1)
    {
        var options = new EmailValidationOptions
        {
            CatchAll = new CatchAllOptions
            {
                ProbeCount = probeCount,
                MaxProbeCount = probeCount,
                MinimumAcceptedProbes = 2
            }
        };
        return new CatchAllDetector(new FakeProbe(status), Microsoft.Extensions.Options.Options.Create(options));
    }

    private static DomainIntelligence DomainData() => new()
    {
        Domain = "example.com",
        DomainExists = true,
        Dns = new DnsLookupResult(DnsStatus.Success, true, [new MxRecord(10, "mx.example.com")], false, TimeSpan.Zero),
        Provider = new ProviderDetectionResult(MailProvider.GenericSmtp, 0.55),
        Disposable = false,
        ObservedAt = DateTimeOffset.UtcNow
    };

    private sealed class FakeProbe(SmtpMailboxStatus status) : ISmtpMailboxProbe
    {
        public Task<SmtpProbeResult> ProbeAsync(string mxHost, string recipient, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SmtpProbeResult(status, null, null, TimeSpan.Zero));
    }
}
