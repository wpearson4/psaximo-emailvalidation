using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class EmailValidatorTests
{
    [Fact]
    public async Task InvalidSyntax_ShortCircuitsNetworkAndReturnsSpecificReason()
    {
        var dns = new FakeDns();
        var validator = CreateValidator(dns);

        var result = await validator.ValidateAsync("not-an-email", new EmailValidationRequest());

        Assert.Equal(EmailValidationStatus.Invalid, result.Status);
        Assert.Contains(ReasonCode.MissingDomain, result.ReasonCodes);
        Assert.Equal(0, dns.Calls);
    }

    [Fact]
    public async Task RepeatedDomain_ReusesDomainCache()
    {
        var dns = new FakeDns();
        var validator = CreateValidator(dns);

        await validator.ValidateAsync("one@example.com", new EmailValidationRequest());
        await validator.ValidateAsync("two@example.com", new EmailValidationRequest());

        Assert.Equal(1, dns.Calls);
    }

    [Fact]
    public async Task NetworkDisabled_DoesNotRunSmtpAndRemainsUnknown()
    {
        var validator = CreateValidator(new FakeDns());

        var result = await validator.ValidateAsync("person@example.com", new EmailValidationRequest(EnableSmtp: false));

        Assert.Equal(SmtpMailboxStatus.NotAttempted, result.Checks.Mailbox);
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(ReasonCode.SmtpDisabled, result.ReasonCodes);
    }

    [Fact]
    public async Task ImplicitMxFallbackIsExposedAsRoutingEvidence()
    {
        var validator = CreateValidator(new FakeDns(usedAddressFallback: true));

        var result = await validator.ValidateAsync("person@example.com", new EmailValidationRequest());

        Assert.True(result.UsedImplicitMxFallback);
        Assert.True(result.Checks.UsedImplicitMxFallback);
        Assert.Contains(ReasonCode.ImplicitMxFallback, result.ReasonCodes);
    }

    [Fact]
    public async Task ChangedMxTopology_ExcludesStaleDomainBehaviorFromActiveHistory()
    {
        var store = new InMemoryValidationObservationStore();
        await store.RecordAsync(new ValidationObservation(
            "example.com", ValidationObservationType.CatchAllProbe, MailProvider.Microsoft365,
            "old.mail.protection.outlook.com", CatchAllStatus.LikelyCatchAll, 0.95,
            SmtpResponseCategory.Accepted, DateTimeOffset.UtcNow, 10,
            RandomRecipientAcceptedCount: 1,
            RandomRecipientProbeCount: 1,
            GatewayProvider: GatewayProvider.MicrosoftExchangeOnlineProtection,
            TopologyFingerprint: "0:old.mail.protection.outlook.com"));
        var validator = CreateValidator(new MicrosoftDns(), observationStore: store);

        var result = await validator.ValidateAsync("person@example.com", new EmailValidationRequest());

        Assert.NotNull(result.HistoricalEvidence);
        Assert.Equal(0, result.HistoricalEvidence.ObservationCount);
    }

    [Fact]
    public async Task RecentMicrosoftDomainProfile_ReusesCatchAllProbeWithinTtl()
    {
        var catchAll = new CountingCatchAll();
        var settings = new EmailValidationOptions
        {
            Smtp = new SmtpOptions { Enabled = true },
            CatchAll = new CatchAllOptions { Enabled = true, CacheMinutes = 60 },
            Dns = new DnsOptions { CacheMinutes = 60 }
        };
        var validator = CreateValidator(new MicrosoftDns(), settings, catchAll: catchAll);

        await validator.ValidateAsync("one@example.com", new EmailValidationRequest(EnableSmtp: true));
        var second = await validator.ValidateAsync(
            "two@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(1, catchAll.Calls);
        Assert.True(second.Diagnostics?.DomainCacheHit);
        Assert.Equal(0, second.Diagnostics?.CatchAllProbes);
    }

    [Fact]
    public async Task AmbiguousPreferredMx_EscalatesAndUsesRecipientSpecificRejection()
    {
        var smtp = new MxSequenceSmtp(new Dictionary<string, SmtpProbeResult>
        {
            ["mx1.example.com"] = MailFromBlocked("mx1.example.com"),
            ["mx2.example.com"] = RecipientRejected("mx2.example.com")
        });
        var settings = LiveSettings();
        var validator = CreateValidator(
            new MultiMxDns(), settings, smtp: smtp,
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(EmailValidationStatus.Invalid, result.Status);
        Assert.Equal(MxConsensus.ConclusiveNegative, result.MxValidation?.Consensus);
        Assert.Equal(["mx1.example.com", "mx2.example.com"], result.MxValidation?.HostsAttempted);
    }

    [Fact]
    public async Task ConflictingMxEvidence_IsUnknownAndLowersReliability()
    {
        var smtp = new MxSequenceSmtp(new Dictionary<string, SmtpProbeResult>
        {
            ["mx1.example.com"] = RecipientAccepted("mx1.example.com"),
            ["mx2.example.com"] = RecipientRejected("mx2.example.com")
        });
        var validator = CreateValidator(
            new MultiMxDns(), LiveSettings(), smtp: smtp,
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Equal(MxConsensus.Conflicting, result.MxValidation?.Consensus);
        Assert.Contains(ReasonCode.MxResultsConflicting, result.ReasonCodes);
        Assert.Equal(VerificationReliabilityLevel.Low, result.ProviderValidation?.VerificationReliabilityLevel);
    }

    private static EmailValidator CreateValidator(
        IDnsMailResolver dns,
        EmailValidationOptions? settings = null,
        IValidationObservationStore? observationStore = null,
        ICatchAllDetector? catchAll = null,
        ISmtpMailboxProbe? smtp = null)
    {
        settings ??= new EmailValidationOptions();
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        IMailProviderStrategy[] strategies =
        [
            new Microsoft365Strategy(), new GoogleWorkspaceStrategy(), new ProofpointStrategy(),
            new MimecastStrategy(), new GenericSmtpStrategy()
        ];
        return new EmailValidator(
            new EmailNormalizer(), dns, new FakeDomainIntelligence(), new FakeEmailIntelligence(), new RoleAccountDetector(options),
            new MailProviderDetector(), smtp ?? new FakeSmtp(), new HealthyProbeSender(), catchAll ?? new FakeCatchAll(), new InMemoryDomainValidationCache(),
            new EmailClassificationEngine(), new MailProviderStrategyResolver(strategies),
            observationStore ?? new InMemoryValidationObservationStore(), new HistoricalSignalAggregator(),
            new ResultEvaluator(), new SmtpSessionBudget(), options, NullLogger<EmailValidator>.Instance);
    }

    private sealed class FakeDomainIntelligence : IDomainIntelligenceEvaluator
    {
        public Task<SupplementalDomainIntelligence> EvaluateAsync(
            string domain,
            DnsLookupResult dns,
            CancellationToken cancellationToken = default) => Task.FromResult(new SupplementalDomainIntelligence(
                DisposableDomainResult.Unknown,
                false,
                ToxicDomainResult.Unknown,
                MxForwardResult.Unknown,
                DomainAgeResult.Unknown,
                new MailInfrastructureResult(MailInfrastructureStatus.Routable, dns.MxRecords.Select(item => item.Host).ToArray(), [], 0.95),
                0));
    }

    private sealed class FakeEmailIntelligence : IEmailIntelligenceEvaluator
    {
        public Task<EmailAddressIntelligence> EvaluateAsync(
            string email,
            string localPart,
            string domain,
            CancellationToken cancellationToken = default) => Task.FromResult(new EmailAddressIntelligence { Email = email });
    }

    private sealed class MicrosoftDns : IDnsMailResolver
    {
        public Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnsLookupResult(
                DnsStatus.Success, true,
                [new MxRecord(0, "tenant.mail.protection.outlook.com")],
                false, TimeSpan.FromMilliseconds(1)));
    }

    private sealed class MultiMxDns : IDnsMailResolver
    {
        public Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnsLookupResult(
                DnsStatus.Success, true,
                [new MxRecord(10, "mx1.example.com"), new MxRecord(20, "mx2.example.com"), new MxRecord(30, "mx3.example.com")],
                false, TimeSpan.Zero));
    }

    private sealed class FakeDns(bool usedAddressFallback = false) : IDnsMailResolver
    {
        public int Calls { get; private set; }

        public Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new DnsLookupResult(
                DnsStatus.Success, true, [new MxRecord(10, "mx.example.com")], usedAddressFallback, TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class FakeSmtp : ISmtpMailboxProbe
    {
        public Task<SmtpProbeResult> ProbeAsync(string mxHost, string recipient, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SmtpProbeResult(SmtpMailboxStatus.Accepted, 250, "ok", TimeSpan.Zero));
    }

    private sealed class MxSequenceSmtp(IReadOnlyDictionary<string, SmtpProbeResult> results) : ISmtpMailboxProbe
    {
        public Task<SmtpProbeResult> ProbeAsync(
            string mxHost, string recipient, CancellationToken cancellationToken = default) =>
            Task.FromResult(results[mxHost]);

        public Task<SmtpProbeResult> ProbeAsync(
            string mxHost, string recipient, MailProvider provider,
            CancellationToken cancellationToken = default) => ProbeAsync(mxHost, recipient, cancellationToken);
    }

    private sealed class HealthyProbeSender : IProbeSenderHealthChecker
    {
        public Task<ProbeSenderHealth> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProbeSenderHealth(
                ProbeSenderHealthStatus.Valid, "probe@validator.example", "validator.example", "Healthy."));
    }

    private sealed class FakeCatchAll : ICatchAllDetector
    {
        public Task<CatchAllDetectionResult> DetectAsync(
            string domain,
            string mxHost,
            MailProvider provider,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CatchAllDetectionResult(CatchAllStatus.NotCatchAll, 1, 0, 1, 0));
    }

    private sealed class CountingCatchAll : ICatchAllDetector
    {
        public int Calls { get; private set; }

        public Task<CatchAllDetectionResult> DetectAsync(
            string domain,
            string mxHost,
            MailProvider provider,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CatchAllDetectionResult(
                CatchAllStatus.LikelyNotCatchAll, 1, 0, 1, 0,
                "Random recipient rejected.", 0.92));
        }
    }

    private sealed class StaticCatchAll(CatchAllStatus status) : ICatchAllDetector
    {
        public Task<CatchAllDetectionResult> DetectAsync(
            string domain, string mxHost, MailProvider provider,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new CatchAllDetectionResult(status, 1, 0, 0, 1, Confidence: 0.2));
    }

    private static EmailValidationOptions LiveSettings() => new()
    {
        Smtp = new SmtpOptions { Enabled = true, ProbeSender = "probe@validator.example", MaxMxAttempts = 3 },
        CatchAll = new CatchAllOptions { Enabled = true, ProbeCount = 1, MaxProbeCount = 1 }
    };

    private static SmtpProbeResult MailFromBlocked(string host)
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.MailFrom, 550, "5.7.1", SmtpResponseCategory.VerificationBlocked,
            SmtpResponseTextClassification.PolicyRejection, 1, MailProvider.GenericSmtp,
            host, 1, DateTimeOffset.UtcNow, "550 5.7.1 Sender rejected");
        var session = new SmtpSessionEvidence(
            SmtpCommand.MailFrom,
            [new(SmtpCommand.MailFrom, 550, "5.7.1", SmtpResponseCategory.VerificationBlocked,
                SmtpResponseTextClassification.PolicyRejection, TimeSpan.Zero)],
            host, TimeSpan.Zero, "probe@validator.example");
        return new(SmtpMailboxStatus.Blocked, 550, evidence.SanitizedResponse, TimeSpan.Zero,
            Evidence: evidence, SessionEvidence: session);
    }

    private static SmtpProbeResult RecipientRejected(string host)
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 550, "5.1.1", SmtpResponseCategory.RecipientRejected,
            SmtpResponseTextClassification.RecipientDoesNotExist, 1, MailProvider.GenericSmtp,
            host, 1, DateTimeOffset.UtcNow, "550 5.1.1 User unknown");
        var session = RecipientSession(host, evidence.Category, 550, "5.1.1",
            SmtpResponseTextClassification.RecipientDoesNotExist);
        return new(SmtpMailboxStatus.Rejected, 550, evidence.SanitizedResponse, TimeSpan.Zero,
            Evidence: evidence, SessionEvidence: session);
    }

    private static SmtpProbeResult RecipientAccepted(string host)
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 250, "2.1.5", SmtpResponseCategory.Accepted,
            SmtpResponseTextClassification.Success, 1, MailProvider.GenericSmtp,
            host, 1, DateTimeOffset.UtcNow, "250 2.1.5 OK");
        var session = RecipientSession(host, evidence.Category, 250, "2.1.5",
            SmtpResponseTextClassification.Success);
        return new(SmtpMailboxStatus.Accepted, 250, evidence.SanitizedResponse, TimeSpan.Zero,
            Evidence: evidence, SessionEvidence: session);
    }

    private static SmtpSessionEvidence RecipientSession(
        string host, SmtpResponseCategory category, int code, string enhanced,
        SmtpResponseTextClassification textClassification) => new(
        category == SmtpResponseCategory.Accepted ? null : SmtpCommand.RcptTo,
        [
            new(SmtpCommand.MailFrom, 250, "2.1.0", SmtpResponseCategory.Accepted,
                SmtpResponseTextClassification.Success, TimeSpan.Zero),
            new(SmtpCommand.RcptTo, code, enhanced, category, textClassification, TimeSpan.Zero)
        ],
        host, TimeSpan.Zero, "probe@validator.example");
}
