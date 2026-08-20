using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class IntelligenceTests
{
    private static readonly IOptions<EmailValidationOptions> Options = Microsoft.Extensions.Options.Options.Create(new EmailValidationOptions());

    [Theory]
    [InlineData("support", true)]
    [InlineData("SUPPORT", true)]
    [InlineData("jane", false)]
    public void RoleAccountDetection_IsConfigurableAndCaseInsensitive(string localPart, bool expected)
    {
        Assert.Equal(expected, new RoleAccountDetector(Options).IsRoleAccount(localPart));
    }

    [Theory]
    [InlineData("mailinator.com", true)]
    [InlineData("MAILINATOR.COM", true)]
    [InlineData("example.com", false)]
    public void DisposableDomainDetection_UsesLocalList(string domain, bool expected)
    {
        Assert.Equal(expected, new DisposableEmailDetector(Options).IsDisposable(domain));
    }

    [Theory]
    [InlineData("example-com.mail.protection.outlook.com", MailProvider.Microsoft365)]
    [InlineData("example-com.a-v1.mx.microsoft", MailProvider.Microsoft365)]
    [InlineData("aspmx.l.google.com", MailProvider.GoogleWorkspace)]
    [InlineData("mx1-us1.ppe-hosted.com", MailProvider.Proofpoint)]
    [InlineData("mx.example.com", MailProvider.GenericSmtp)]
    public void ProviderDetection_InfersFromMxHost(string host, MailProvider expected)
    {
        Assert.Equal(expected, new MailProviderDetector().Detect([new MxRecord(10, host)]));
    }

    [Fact]
    public void ProviderDetection_ReturnsConfidenceAndMatchedSignature()
    {
        var result = new MailProviderDetector().DetectWithConfidence(
            [new MxRecord(0, "tenant.mail.protection.outlook.com")]);

        Assert.Equal(MailProvider.Microsoft365, result.Provider);
        Assert.InRange(result.Confidence, 0.95, 1.0);
        Assert.Equal("mail.protection.outlook.com", result.MatchedSignature);
        Assert.Equal(ProviderFamily.Microsoft365, result.Family);
        Assert.Equal(GatewayProvider.MicrosoftExchangeOnlineProtection, result.GatewayProvider);
        Assert.Equal(MailProvider.Unknown, result.MailboxProvider);
        Assert.Equal("tenant.mail.protection.outlook.com", result.MxHost);
    }

    [Fact]
    public void ProviderDetection_RecognizesMicrosoftConsumerMxBoundary()
    {
        var result = new MailProviderDetector().DetectWithConfidence(
            [new MxRecord(2, "hotmail-com.olc.protection.outlook.com")]);

        Assert.Equal(MailProvider.Microsoft365, result.Provider);
        Assert.Equal("olc.protection.outlook.com", result.MatchedSignature);
        Assert.Equal(GatewayProvider.MicrosoftExchangeOnlineProtection, result.GatewayProvider);
    }

    [Theory]
    [InlineData("mail.protection.outlook.com.evil.example")]
    [InlineData("tenant.protection.outlook.example")]
    [InlineData("tenant.mx.microsoft.example")]
    [InlineData("notmx.microsoft")]
    public void ProviderDetection_DoesNotClassifyMicrosoftLookingHostnames(string host)
    {
        var result = new MailProviderDetector().DetectWithConfidence([new MxRecord(10, host)]);

        Assert.Equal(MailProvider.GenericSmtp, result.Provider);
        Assert.NotEqual(GatewayProvider.MicrosoftExchangeOnlineProtection, result.GatewayProvider);
    }

    [Fact]
    public void ProviderDetection_UsesPreferredThirdPartyGatewayAndDoesNotBypassIt()
    {
        var result = new MailProviderDetector().DetectWithConfidence(
        [
            new MxRecord(0, "us-smtp-inbound-1.mimecast.com"),
            new MxRecord(20, "tenant.mail.protection.outlook.com")
        ]);

        Assert.Equal(MailProvider.Mimecast, result.Provider);
        Assert.Equal(GatewayProvider.Mimecast, result.GatewayProvider);
        Assert.Equal(MailProvider.Unknown, result.MailboxProvider);
        Assert.Equal("us-smtp-inbound-1.mimecast.com", result.MxHost);
    }

    [Fact]
    public void ProviderDetection_ChangesTopologyFingerprintWhenPublishedMxChanges()
    {
        var detector = new MailProviderDetector();
        var oldTopology = detector.DetectWithConfidence(
            [new MxRecord(0, "tenant.mail.protection.outlook.com")]);
        var newTopology = detector.DetectWithConfidence(
            [new MxRecord(0, "tenant.a-v1.mx.microsoft")]);

        Assert.NotEqual(oldTopology.TopologyFingerprint, newTopology.TopologyFingerprint);
    }

    [Fact]
    public void ProviderDetection_UsesGenericFallbackForUnknownMx()
    {
        var result = new MailProviderDetector().DetectWithConfidence([new MxRecord(10, "mx.example.net")]);

        Assert.Equal(MailProvider.GenericSmtp, result.Provider);
        Assert.Equal(0.55, result.Confidence);
    }
}
