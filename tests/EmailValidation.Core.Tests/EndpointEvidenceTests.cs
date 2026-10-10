using System.Security.Cryptography;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class EndpointEvidenceTests
{
    [Theory]
    [InlineData(false, false, EmailValidationStatus.Valid)]
    [InlineData(true, false, EmailValidationStatus.Unknown)]
    [InlineData(true, true, EmailValidationStatus.Unknown)]
    public async Task ControlsOnFirstMx_QualifyOnlyAcceptanceOnThatEndpoint(bool fallback, bool equalPreference, EmailValidationStatus expected)
    {
        var settings = new EmailValidationOptions { Smtp = new() { Enabled = true, MaxMxAttempts = 2 } };
        var smtp = new ScriptedSmtp(fallback, false);
        var result = await EmailValidatorTests.CreateValidator(new Dns(equalPreference), settings,
            smtp: smtp, catchAll: new CatchAllDetector(smtp, Options.Create(settings)))
            .ValidateAsync("person@example.test", new EmailValidationRequest(true, true));
        Assert.Equal(expected, result.Status);
        Assert.Equal(fallback ? "mx-b.example.test" : "mx-a.example.test", result.SelectedMx);
        Assert.Equal("mx-a.example.test", result.DomainIntelligence!.CatchAll.ControlScope!.MxHost);
        Assert.Equal(2, smtp.Controls.Count);
        if (fallback)
        {
            Assert.Equal(CatchAllReasonCode.EndpointEvidenceMismatch, result.CatchAllEvidence!.ReasonCode);
            Assert.NotEqual(MxConsensus.ConclusivePositive, result.MxValidation!.Consensus);
            Assert.DoesNotContain(ReasonCode.MailboxAccepted, result.ReasonCodes);
        }
    }

    [Fact]
    public async Task TemporaryEqualPreferencePeer_KeepsAcceptanceInconclusive()
    {
        var settings = new EmailValidationOptions { Smtp = new() { Enabled = true, MaxMxAttempts = 2 } };
        var smtp = new ScriptedSmtp(false, false, true);
        var result = await EmailValidatorTests.CreateValidator(new Dns(true), settings,
            smtp: smtp, catchAll: new CatchAllDetector(smtp, Options.Create(settings)))
            .ValidateAsync("person@example.test", new EmailValidationRequest(true));
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.NotEqual(MxConsensus.ConclusivePositive, result.MxValidation!.Consensus);
    }

    [Fact]
    public async Task PrefixBiasedSyntheticEndpoint_DoesNotProduceFalseRecipientSpecificEvidence()
    {
        var settings = new EmailValidationOptions { Smtp = new() { Enabled = true } };
        var smtp = new ScriptedSmtp(false, true);
        var result = await EmailValidatorTests.CreateValidator(new Dns(false), settings,
            smtp: smtp, catchAll: new CatchAllDetector(smtp, Options.Create(settings)))
            .ValidateAsync("person@example.test", new EmailValidationRequest(true));
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Equal(CatchAllReasonCode.AcceptAllCandidate, result.CatchAllEvidence!.ReasonCode);
        Assert.All(smtp.Controls, recipient => Assert.DoesNotContain("dwcheck-", recipient, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("preference")]
    [InlineData("provider")]
    [InlineData("gateway")]
    [InlineData("topology")]
    [InlineData("strategy")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("stage")]
    [InlineData("mixed")]
    [InlineData("missing-scope")]
    public void ChangedOrIncompleteProvenance_CannotStrengthenAcceptance(string change)
    {
        var now = DateTimeOffset.UtcNow;
        var domain = Domain();
        var target = Probe("mx-a.example.test", SmtpResponseCategory.Accepted, now);
        domain = WithControls(domain, now.AddSeconds(-2));
        Assert.True(EndpointControlEvidencePolicy.HasRecipientSpecificControls(domain, target));
        var scope = domain.CatchAll.ControlScope!;
        domain = change switch
        {
            "endpoint" => domain with { CatchAll = domain.CatchAll with { ControlScope = scope with { MxHost = "mx-b.example.test" } } },
            "preference" => domain with { CatchAll = domain.CatchAll with { ControlScope = scope with { Preference = 99 } } },
            "provider" => domain with { Provider = domain.Provider with { Provider = MailProvider.Microsoft365 } },
            "gateway" => domain with { Provider = domain.Provider with { GatewayProvider = GatewayProvider.Proofpoint } },
            "topology" => domain with { Dns = domain.Dns with { MxRecords = [new(10, "new.example.test")] } },
            "strategy" => domain with { StrategyVersion = "changed" },
            "expired" => domain with { CatchAll = domain.CatchAll with { EvidenceExpiresAt = now } },
            "future" => domain with { CatchAll = domain.CatchAll with { ObservedAt = now.AddSeconds(1) } },
            "stage" => domain with { CatchAll = domain.CatchAll with { ProbeResults = [target with { SessionEvidence = null }] } },
            "mixed" => domain with { CatchAll = domain.CatchAll with { ProbeResults = [Probe("mx-b.example.test", SmtpResponseCategory.RecipientRejected, now.AddSeconds(-2))] } },
            _ => domain with { CatchAll = domain.CatchAll with { ControlScope = null } }
        };
        Assert.False(EndpointControlEvidencePolicy.HasRecipientSpecificControls(domain, target));
    }

    internal static DomainIntelligence Domain() => new()
    {
        Domain = "example.test", DomainExists = true,
        Dns = new(DnsStatus.Success, true, [new(10, "mx-a.example.test"), new(20, "mx-b.example.test")], false, TimeSpan.Zero),
        Provider = new(MailProvider.GenericSmtp, .9), StrategyVersion = "test-v3"
    };

    internal static DomainIntelligence WithControls(DomainIntelligence domain, DateTimeOffset at, int count = 1) => domain with
    {
        CatchAll = new(CatchAllStatus.NotCatchAll, count, 0, count, 0, Confidence: .95)
        {
            ObservedAt = at, EvidenceExpiresAt = at.AddMinutes(5),
            EvidenceContractVersion = CatchAllDetectionResult.CurrentRecipientBehaviorEvidenceContractVersion,
            ControlScope = new(SmtpRecipientEvidencePolicy.NormalizeHost(domain.MxRecords[0].Host), domain.MxRecords[0].Preference,
                domain.Provider.Provider, domain.Provider.GatewayProvider,
                EndpointControlEvidencePolicy.TopologyFingerprint(domain), domain.StrategyVersion, "fixture-acquisition"),
            ProbeResults = Enumerable.Range(0, count).Select(_ => Probe(domain.MxRecords[0].Host, SmtpResponseCategory.RecipientRejected, at)).ToArray()
        }
    };

    internal static SmtpProbeResult Probe(string host, SmtpResponseCategory category, DateTimeOffset at)
    {
        var accepted = category == SmtpResponseCategory.Accepted;
        var code = accepted ? 250 : category == SmtpResponseCategory.RecipientRejected ? 550 : 451;
        var enhanced = accepted ? "2.1.5" : code == 550 ? "5.1.1" : "4.3.0";
        var text = accepted ? SmtpResponseTextClassification.Success : SmtpResponseTextClassification.RecipientDoesNotExist;
        return new(accepted ? SmtpMailboxStatus.Accepted : code == 550 ? SmtpMailboxStatus.Rejected : SmtpMailboxStatus.TemporaryFailure,
            code, enhanced, TimeSpan.Zero, Evidence: new(SmtpCommand.RcptTo, code, enhanced, category, text, 1,
                MailProvider.GenericSmtp, host, 1, at), SessionEvidence: new(null,
                [new(SmtpCommand.MailFrom, 250, "2.1.0", SmtpResponseCategory.Accepted, SmtpResponseTextClassification.Success, TimeSpan.Zero),
                 new(SmtpCommand.RcptTo, code, enhanced, category, text, TimeSpan.Zero)], host, TimeSpan.Zero, "probe@validator.example"));
    }

    private sealed class Dns(bool equalPreference) : IDnsMailResolver
    {
        public Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default) => Task.FromResult(
            new DnsLookupResult(DnsStatus.Success, true,
                [new(10, "mx-a.example.test"), new(equalPreference ? 10 : 20, "mx-b.example.test")], false, TimeSpan.Zero));
    }

    private sealed class ScriptedSmtp(bool fallback, bool prefixBiased, bool peerTemporary = false) : ISmtpMailboxProbe
    {
        public List<string> Controls { get; } = [];
        public Task<SmtpProbeResult> ProbeAsync(string mxHost, string recipient, CancellationToken cancellationToken = default)
        {
            var control = !recipient.StartsWith("person@", StringComparison.Ordinal);
            if (control) Controls.Add(recipient);
            var category = control
                ? prefixBiased && !recipient.StartsWith("dwcheck-", StringComparison.Ordinal)
                    ? SmtpResponseCategory.Accepted : SmtpResponseCategory.RecipientRejected
                : fallback && mxHost == "mx-a.example.test" || peerTemporary && mxHost == "mx-b.example.test"
                    ? SmtpResponseCategory.TemporaryFailure : SmtpResponseCategory.Accepted;
            return Task.FromResult(Probe(mxHost, category, DateTimeOffset.UtcNow));
        }
    }
}

internal static class TestRoutingAttestations
{
    private static readonly RSA Key = RSA.Create(2048);
    internal static RoutingAttestationAuthority Authority => new()
        { Id = "fixture-authority", PublicKeyPem = Key.ExportSubjectPublicKeyInfoPem(), AuthorizedDomains = ["example.com", "example.test"] };
    internal static SignedRoutingAttestation Sign(DomainIntelligence domain, DateTimeOffset? issuedAt = null)
    {
        var at = issuedAt ?? DateTimeOffset.UtcNow.AddMinutes(-10);
        var unsigned = new SignedRoutingAttestation("fixture-authority", Guid.NewGuid().ToString("N"), domain.Domain,
            EndpointControlEvidencePolicy.TopologyFingerprint(domain), at, at.AddHours(2), "");
        lock (Key) return unsigned with { Signature = Convert.ToBase64String(Key.SignData(
            RoutingAttestationPolicy.SigningPayload(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
    }
}
