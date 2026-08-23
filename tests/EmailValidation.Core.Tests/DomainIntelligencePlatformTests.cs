using EmailValidation.Core;
using EmailValidation.Application;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class DomainIntelligencePlatformTests
{
    [Theory]
    [InlineData("sales", RoleAddressType.Sales)]
    [InlineData("info", RoleAddressType.Information)]
    [InlineData("support+product", RoleAddressType.Support)]
    [InlineData("security", RoleAddressType.Security)]
    public void RoleDetection_ReturnsTypedRiskMetadataWithoutSpamTrapClaim(
        string localPart,
        RoleAddressType expected)
    {
        var detector = new RoleAccountDetector(Options.Create(new EmailValidationOptions()));

        var result = detector.Detect(new NormalizedEmailAddress(
            $"{localPart}@example.test", localPart, "example.test"));

        Assert.True(result.IsRoleAddress);
        Assert.Equal(expected, result.RoleType);
        Assert.DoesNotContain("spam", result.Evidence!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoleDetection_NormalPersonIsNotRole()
    {
        var detector = new RoleAccountDetector(Options.Create(new EmailValidationOptions()));

        var result = detector.Detect(new NormalizedEmailAddress(
            "normal.person@example.test", "normal.person", "example.test"));

        Assert.False(result.IsRoleAddress);
        Assert.Equal(RoleAddressType.None, result.RoleType);
    }

    [Fact]
    public void AuthenticationParser_SeparatesValidAbsentAndMalformedSpf()
    {
        var present = EmailAuthenticationAnalyzer.ParseSpf(Response("v=spf1 include:_spf.example.test -all"));
        var absent = EmailAuthenticationAnalyzer.ParseSpf(Response("unrelated=value"));
        var malformed = EmailAuthenticationAnalyzer.ParseSpf(Response("v=spf1 -all", "v=spf1 ~all"));

        Assert.Equal(AuthenticationRecordState.Valid, present.State);
        Assert.Equal("-all", present.AllMechanism);
        Assert.Equal(AuthenticationRecordState.NotPresent, absent.State);
        Assert.Equal(AuthenticationRecordState.Invalid, malformed.State);
    }

    [Fact]
    public void AuthenticationParser_ParsesDmarcAndRejectsMalformedPolicy()
    {
        var present = EmailAuthenticationAnalyzer.ParseDmarc(
            Response("v=DMARC1; p=reject; sp=quarantine; pct=75"));
        var absent = EmailAuthenticationAnalyzer.ParseDmarc(Response());
        var malformed = EmailAuthenticationAnalyzer.ParseDmarc(Response("v=DMARC1; p=discard"));

        Assert.Equal(AuthenticationRecordState.Valid, present.State);
        Assert.Equal(DmarcPolicy.Reject, present.Policy);
        Assert.Equal(DmarcPolicy.Quarantine, present.SubdomainPolicy);
        Assert.Equal(75, present.Percentage);
        Assert.Equal(AuthenticationRecordState.NotPresent, absent.State);
        Assert.Equal(AuthenticationRecordState.Invalid, malformed.State);
    }

    [Fact]
    public void DkimWithoutSelectors_RemainsNotEvaluatedRatherThanAbsent()
    {
        Assert.Equal(DkimObservationState.NotEvaluated, DkimIntelligence.NotEvaluated.State);
        Assert.Empty(DkimIntelligence.NotEvaluated.ObservedSelectors);
    }

    [Theory]
    [InlineData(true, 0, true, DnsSecurityState.Secure)]
    [InlineData(false, 0, false, DnsSecurityState.NotPresent)]
    [InlineData(false, 5, false, DnsSecurityState.Indeterminate)]
    public async Task DnsSecurity_MapsResolverValidationStateHonestly(
        bool authenticated,
        int responseCode,
        bool hasDnsKey,
        DnsSecurityState expected)
    {
        var analyzer = new DnsSecurityAnalyzer(
            new FakeDnsWireClient([Wire(responseCode, authenticated, hasDnsKey)]),
            Options.Create(new EmailValidationOptions()),
            NullLogger<DnsSecurityAnalyzer>.Instance);

        var result = await analyzer.AnalyzeAsync("example.test");

        Assert.Equal(expected, result.State);
    }

    [Fact]
    public async Task DnsSecurity_ServfailWithUncheckedDnsKeyIsBogus()
    {
        var analyzer = new DnsSecurityAnalyzer(
            new FakeDnsWireClient([Wire(2, false, false), Wire(0, false, true)]),
            Options.Create(new EmailValidationOptions()),
            NullLogger<DnsSecurityAnalyzer>.Instance);

        var result = await analyzer.AnalyzeAsync("example.test");

        Assert.Equal(DnsSecurityState.Bogus, result.State);
        Assert.Equal(IntelligenceAvailability.Degraded, result.Availability);
    }

    [Fact]
    public async Task DnsSecurity_LookupFailureIsIndeterminateNotMailboxFailure()
    {
        var analyzer = new DnsSecurityAnalyzer(
            new ThrowingDnsWireClient(),
            Options.Create(new EmailValidationOptions()),
            NullLogger<DnsSecurityAnalyzer>.Instance);

        var result = await analyzer.AnalyzeAsync("example.test");

        Assert.Equal(DnsSecurityState.Indeterminate, result.State);
        Assert.Equal(IntelligenceAvailability.Failed, result.Availability);
    }

    [Fact]
    public void SmtpBannerDetection_IsEvidenceAndDoesNotRewritePublishedGateway()
    {
        var evidence = new SmtpSessionEvidence(
            null, [], "mx.gateway.test", TimeSpan.Zero, "probe@example.test",
            "220 mail.protection.outlook.com Microsoft ESMTP", "tenant.outlook.com");

        var detected = new SmtpBannerProviderDetector().Detect(evidence);

        Assert.Equal(MailProvider.Microsoft365, detected.SmtpObservedProvider);
        Assert.True(detected.SmtpEvidenceConfidence > 0.8);
        Assert.Contains("SmtpGreetingOrEhlo", detected.Evidence!);
    }

    [Fact]
    public async Task DomainSingleFlight_CollapsesConcurrentDomainAndCatchAllWork()
    {
        var routing = new CountingRoutingAnalyzer();
        var catchAll = new CountingCatchAllDetector();
        using var service = CreateService(routing, catchAll, new StickyDomainCache(), FreshOptions());

        var requests = Enumerable.Range(0, 25)
            .Select(index => service.AcquireAsync("Example.Test", true))
            .ToArray();
        var results = await Task.WhenAll(requests);

        Assert.Equal(1, routing.Calls);
        Assert.Equal(1, catchAll.Calls);
        Assert.All(results, result => Assert.Equal("example.test", result.Intelligence.Domain));
    }

    [Fact]
    public async Task DomainLifecycle_TracksTopologyChangeAndInvalidatesCatchAll()
    {
        var options = FreshOptions();
        options.DomainIntelligence.PersistentFreshnessHours = 0;
        options.Dns.CacheMinutes = 0;
        var routing = new ChangingRoutingAnalyzer();
        var cache = new StickyDomainCache();
        using var service = CreateService(routing, new CountingCatchAllDetector(), cache, options);

        var first = await service.AcquireAsync("example.test", true);
        var second = await service.AcquireAsync("example.test", false);

        Assert.NotEqual(first.Intelligence.MxTopologyFingerprint, second.Intelligence.MxTopologyFingerprint);
        Assert.Equal(1, second.Intelligence.ChangeCount);
        Assert.NotNull(second.Intelligence.LastChangedUtc);
        Assert.Equal(CatchAllStatus.NotAttempted, second.Intelligence.CatchAll.Status);
    }

    [Fact]
    public void MongoOlderStructuredDocument_DeserializesWithoutNewFieldsOrPayload()
    {
        var document = new MongoValidationIntelligenceStore.DomainIntelligenceDocument
        {
            Id = "legacy.test",
            Domain = "legacy.test",
            NormalizedDomain = "legacy.test",
            MxRecords = [new MongoValidationIntelligenceStore.MxRecordDocument
                { Preference = 10, Host = "mx.legacy.test" }],
            MxTopologyFingerprint = "10:mx.legacy.test",
            Provider = MailProvider.GenericSmtp,
            ProviderConfidence = 0.55,
            CatchAllStatus = CatchAllStatus.Unknown,
            LastObservedAt = DateTime.UtcNow.AddHours(-1),
            EvidenceFreshUntil = DateTime.UtcNow.AddHours(1),
            ProviderStrategyVersion = "legacy-1"
        };

        var restored = document.ToModel();

        Assert.NotNull(restored);
        Assert.Equal("mx.legacy.test", restored!.MxRecords.Single().Host);
        Assert.Equal(DnsSecurityState.Unknown, restored.DnsSecurity.State);
        Assert.Equal(DkimObservationState.Unknown, restored.Authentication.Dkim.State);
    }

    [Fact]
    public async Task TrustedSpamTrapProvider_UsesKnownOnlyForAttributedDatasetMatch()
    {
        var options = Options.Create(new EmailValidationOptions
        {
            Intelligence = new IntelligenceOptions { KnownSpamTrapAddresses = ["trap@example.test"] }
        });
        var provider = new ConfiguredSpamTrapRiskProvider(new SpamTrapRiskDetector(options));
        var context = new EmailRiskContext(
            "trap@example.test", EmailValidationStatus.Valid, 0.95,
            new EmailValidationChecks(), null, null);

        var result = await provider.EvaluateAsync(context);

        Assert.Equal(SpamTrapRiskLevel.Known, result.Level);
        Assert.Equal(SpamTrapEvidenceKind.TrustedDatasetMatch, result.EvidenceKind);
    }

    [Fact]
    public async Task RiskProviderFailure_DegradesToUnknownWithoutChangingMailboxAssessment()
    {
        var service = new EmailRiskIntelligence([new ThrowingRiskSource()]);
        var context = new EmailRiskContext(
            "person@example.test", EmailValidationStatus.Valid, 0.98,
            new EmailValidationChecks(), null, null);

        var result = await service.EvaluateAsync(context);

        Assert.Equal(EmailValidationStatus.Valid, result.DeliverabilityStatus);
        Assert.Equal(MailingRiskLevel.Unknown, result.MailingRisk);
        Assert.Empty(result.RiskReasons);
    }

    private static DnsWireResponse Response(params string[] text) => new(
        0, false, text.Select(_ => (ushort)DnsRecordType.Txt).ToArray(), text);

    private static DnsWireResponse Wire(int responseCode, bool authenticated, bool hasDnsKey) => new(
        responseCode,
        authenticated,
        hasDnsKey ? [(ushort)DnsRecordType.DnsKey] : [],
        []);

    private static EmailValidationOptions FreshOptions() => new()
    {
        Smtp = new SmtpOptions { Enabled = true },
        CatchAll = new CatchAllOptions
        {
            Enabled = true,
            ProbeCount = 1,
            MaxProbeCount = 1,
            MinimumAcceptedProbes = 1,
            CacheMinutes = 60,
            MinimumReusableConfidence = 0.9
        },
        DomainIntelligence = new DomainIntelligenceOptions
        {
            PersistentFreshnessHours = 24,
            MaximumConcurrentAnalyses = 4,
            PolicyVersion = "test-1"
        }
    };

    private static DomainIntelligenceService CreateService(
        IMailRoutingAnalyzer routing,
        ICatchAllDetector catchAll,
        IDomainValidationCache cache,
        EmailValidationOptions settings)
    {
        var options = Options.Create(settings);
        return new DomainIntelligenceService(
            routing,
            new StaticDnsSecurityAnalyzer(),
            new StaticAuthenticationAnalyzer(),
            new StaticDisposableProvider(),
            new StaticSupplementalEvaluator(),
            new MailProviderDetector(),
            catchAll,
            cache,
            new ValidationPlanBuilder(options),
            new DomainIntelligenceFreshnessPolicy(options),
            new ValidationPersistenceMetrics(),
            options,
            TimeProvider.System,
            NullLogger<DomainIntelligenceService>.Instance);
    }

    private sealed class CountingRoutingAnalyzer : IMailRoutingAnalyzer
    {
        public int Calls;

        public async Task<MailRoutingIntelligence> AnalyzeAsync(
            string domain,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(20, cancellationToken);
            return Routing(domain, "mx.example.test");
        }
    }

    private sealed class FakeDnsWireClient(IEnumerable<DnsWireResponse> responses) : IDnsWireQueryClient
    {
        private readonly Queue<DnsWireResponse> _responses = new(responses);

        public Task<DnsWireResponse> QueryAsync(
            string name,
            DnsRecordType type,
            bool dnssec,
            bool checkingDisabled,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ThrowingDnsWireClient : IDnsWireQueryClient
    {
        public Task<DnsWireResponse> QueryAsync(
            string name,
            DnsRecordType type,
            bool dnssec,
            bool checkingDisabled,
            CancellationToken cancellationToken) => throw new IOException("Resolver unavailable.");
    }

    private sealed class ThrowingRiskSource : IRiskDataSource
    {
        public Task<RiskDataResult> LookupAsync(
            EmailRiskContext context,
            CancellationToken cancellationToken = default) => throw new IOException("Provider unavailable.");
    }

    private sealed class ChangingRoutingAnalyzer : IMailRoutingAnalyzer
    {
        private int _calls;

        public Task<MailRoutingIntelligence> AnalyzeAsync(
            string domain,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var host = Interlocked.Increment(ref _calls) == 1 ? "mx1.example.test" : "mx2.example.test";
            return Task.FromResult(Routing(domain, host));
        }
    }

    private sealed class StaticDnsSecurityAnalyzer : IDnsSecurityAnalyzer
    {
        public Task<DnsSecurityIntelligence> AnalyzeAsync(
            string domain,
            CancellationToken cancellationToken = default) => Task.FromResult(new DnsSecurityIntelligence(
                DnsSecurityState.Secure, IntelligenceAvailability.Available, DateTimeOffset.UtcNow));
    }

    private sealed class StaticAuthenticationAnalyzer : IEmailAuthenticationAnalyzer
    {
        public Task<EmailAuthenticationIntelligence> AnalyzeAsync(
            string domain,
            CancellationToken cancellationToken = default) => Task.FromResult(new EmailAuthenticationIntelligence(
                new SpfIntelligence(AuthenticationRecordState.Valid, "-all", "v=spf1 -all"),
                new DmarcIntelligence(AuthenticationRecordState.Valid, DmarcPolicy.Reject),
                DkimIntelligence.NotEvaluated,
                IntelligenceAvailability.Available,
                DateTimeOffset.UtcNow));
    }

    private sealed class StaticDisposableProvider : IDisposableEmailDomainProvider
    {
        public ValueTask<DisposableDomainResult> GetAsync(
            string domain,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DisposableDomainResult.Unknown);
    }

    private sealed class StaticSupplementalEvaluator : IDomainIntelligenceEvaluator
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
                new MailInfrastructureResult(
                    MailInfrastructureStatus.Routable,
                    dns.MxRecords.Select(record => record.Host).ToArray(),
                    [],
                    0.9),
                0));
    }

    private sealed class CountingCatchAllDetector : ICatchAllDetector
    {
        public int Calls;

        public async Task<CatchAllDetectionResult> DetectAsync(
            string domain,
            string mxHost,
            MailProvider provider,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(20, cancellationToken);
            return new CatchAllDetectionResult(
                CatchAllStatus.LikelyCatchAll, 1, 1, 0, 0,
                "Randomized recipient accepted.", 0.95)
            {
                ReasonCode = CatchAllReasonCode.RandomRecipientsAccepted
            };
        }
    }

    private sealed class StickyDomainCache : IDomainValidationCache
    {
        private DomainIntelligence? _value;
        private DateTimeOffset _expiresAt;

        public int Count => _value is null ? 0 : 1;

        public bool TryGet(string domain, out DomainIntelligence? data)
        {
            data = _expiresAt > DateTimeOffset.UtcNow ? _value : null;
            return data is not null;
        }

        public void Store(DomainIntelligence data, TimeSpan lifetime)
        {
            _value = data;
            _expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        }

        public Task<DomainIntelligence?> GetAsync(
            string domain,
            CancellationToken cancellationToken = default) => Task.FromResult(_value);

        public Task StoreAsync(
            DomainIntelligence data,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default)
        {
            Store(data, lifetime);
            return Task.CompletedTask;
        }
    }

    private static MailRoutingIntelligence Routing(string domain, string host) => new(
        DnsStatus.Success,
        true,
        [new MxRecord(10, host)],
        false,
        false,
        [],
        [],
        TimeSpan.FromMinutes(5),
        DateTimeOffset.UtcNow);
}
