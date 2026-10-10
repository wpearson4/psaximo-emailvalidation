using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class ProviderCapabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MailProvider.GoogleWorkspace, "gmail.com", "Gmail")]
    [InlineData(MailProvider.GoogleWorkspace, "GOOGLEMAIL.COM.", "Gmail")]
    [InlineData(MailProvider.GoogleWorkspace, "business.test", "GoogleWorkspace")]
    [InlineData(MailProvider.MicrosoftConsumer, "outlook.com", "MicrosoftConsumer")]
    [InlineData(MailProvider.Microsoft365, "business.test", "Microsoft365")]
    [InlineData(MailProvider.Yahoo, "aol.com", "AOL")]
    [InlineData(MailProvider.Yahoo, "yahoo.com", "Yahoo")]
    [InlineData(MailProvider.GenericSmtp, "business.test", "Generic")]
    public void ConsumerAndEnterpriseProfiles_AreExplicit(MailProvider provider, string domain, string key) =>
        Assert.Equal(key, ProviderCapabilityPolicy.Key(provider, domain));

    [Fact]
    public void ConsumerOverride_DoesNotChangeEnterprisePolicy()
    {
        var settings = Settings();
        settings.ProviderCapabilities.Profiles["Gmail"] = new() { ProbeMailbox = false };
        settings.ProviderCapabilities.CanaryProviders = ["Gmail"];
        Approve(settings);
        var resolver = new ProviderPolicyResolver(Options.Create(settings));
        Assert.False(resolver.Resolve(MailProvider.GoogleWorkspace, "gmail.com").Capabilities.ProbeMailbox);
        Assert.True(resolver.Resolve(MailProvider.GoogleWorkspace, "business.test").Capabilities.ProbeMailbox);
    }

    [Fact]
    public void EnforcedPolicy_RequiresExactReviewedConfiguration_AndRollbackInvalidatesEvidence()
    {
        var settings = Settings();
        settings.Smtp.Enabled = false;
        settings.Revalidation.Enabled = false;
        var validator = new EmailValidationOptionsValidator();
        Assert.True(validator.Validate(null, settings).Succeeded);
        var version = ProviderCapabilityPolicy.StrategyVersion(settings);
        settings.ProviderCapabilities.Profiles["Generic"] = new() { NonDiscriminationRefreshMinutes = 30 };
        Assert.True(validator.Validate(null, settings).Failed);
        Assert.False(ProviderCapabilityPolicy.Resolve(settings, MailProvider.GenericSmtp).ReuseConfirmedNonDiscrimination);
        Approve(settings);
        Assert.True(validator.Validate(null, settings).Succeeded);
        Assert.NotEqual(version, ProviderCapabilityPolicy.StrategyVersion(settings));
        var domain = Domain(settings);
        settings.ProviderCapabilities.Mode = ProviderCapabilityMode.Shadow;
        Assert.True(Plan(settings, domain).RefreshDomainIntelligence);
        Assert.NotEqual(domain.StrategyVersion, ProviderCapabilityPolicy.StrategyVersion(settings));
    }

    [Fact]
    public void Fingerprint_IsStableAcrossDictionaryOrder_AndExcludesApprovalMetadata()
    {
        var first = new ProviderCapabilityOptions { Profiles = new() { ["Gmail"] = new(), ["Generic"] = new() } };
        var second = new ProviderCapabilityOptions
        {
            Profiles = new() { ["generic"] = new(), ["gmail"] = new() },
            Mode = ProviderCapabilityMode.Enforced, ApprovalReference = "review", ApprovedPolicyHash = "approval"
        };
        Assert.Equal(ProviderCapabilityPolicy.Fingerprint(first), ProviderCapabilityPolicy.Fingerprint(second));
        second.Profiles["gmail"] = new() { AllowSmtpUtf8 = false };
        Assert.NotEqual(ProviderCapabilityPolicy.Fingerprint(first), ProviderCapabilityPolicy.Fingerprint(second));
    }

    [Fact]
    public async Task RepeatedMailboxes_ReuseQualifiedAcceptAll_WithoutProbesOrInventedEvidenceClocks()
    {
        var settings = Settings();
        var domain = Domain(settings);
        var smtp = new CountingSmtp();
        var controls = new CountingControls();
        var validator = EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: smtp, catchAll: controls, cache: new Cache(domain), clock: new Clock());
        foreach (var address in new[] { "one@example.test", "two@example.test" })
        {
            var result = await validator.ValidateAsync(address, new(true, true));
            Assert.Equal(EmailValidationStatus.Unknown, result.Status);
            Assert.Equal(UnknownCause.NonDiscriminatingSmtpEndpoint, result.UnknownContext?.Cause);
            Assert.False(result.UnknownContext!.Retryable);
            Assert.Contains(ReasonCode.NonDiscriminationEvidenceReused, result.ReasonCodes);
            Assert.DoesNotContain(ReasonCode.SmtpDisabled, result.ReasonCodes);
            Assert.False(result.ProbeAttempted);
            Assert.Null(result.MailboxEvidenceObservedAt);
            Assert.Equal(domain.CatchAll.ObservedAt, result.CatchAllEvidence!.ObservedAt);
            Assert.Equal(Now.AddMinutes(50), result.ProviderCapabilities!.NextUsefulCheckAt);
            Assert.Equal(ValidationResultSource.PersistentDomainIntelligence, result.Metadata!.ResultSource);
            Assert.False(new RevalidationPolicy(new ProviderPolicyResolver(Options.Create(settings)), Options.Create(settings))
                .Evaluate(result with { ReasonCodes = result.ReasonCodes.Append(ReasonCode.RetryRecommended).ToArray() }, new(1)).ShouldRetry);
        }
        Assert.Equal(0, smtp.Calls);
        Assert.Equal(0, controls.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shadow_KeepsLiveProbesAndReportsClassificationDisagreement(bool rejection)
    {
        var settings = Settings();
        settings.ProviderCapabilities.Mode = ProviderCapabilityMode.Shadow;
        var smtp = new CountingSmtp(rejection);
        var result = await EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: smtp, cache: new Cache(Domain(settings)), clock: new Clock())
            .ValidateAsync("person@example.test", new(true));
        Assert.Equal(1, smtp.Calls);
        Assert.True(result.ProviderCapabilities!.WouldSkipMailbox);
        Assert.False(result.ProviderCapabilities.Applied);
        Assert.Equal(rejection, result.ProviderCapabilities.ShadowStatusDisagrees);
        Assert.Equal(rejection ? EmailValidationStatus.Invalid : EmailValidationStatus.Unknown, result.Status);
        Assert.DoesNotContain(ReasonCode.NonDiscriminationEvidenceReused, result.ReasonCodes);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("topology")]
    [InlineData("multiple_mx")]
    [InlineData("provider")]
    [InlineData("strategy")]
    [InlineData("weak")]
    [InlineData("mixed")]
    [InlineData("legacy")]
    [InlineData("scope")]
    [InlineData("refresh_inconclusive")]
    [InlineData("candidate")]
    [InlineData("no_expiry")]
    public void UnqualifiedEvidence_NeverSkipsMailbox(string fault)
    {
        var settings = Settings();
        var domain = Domain(settings);
        domain = fault switch
        {
            "expired" => domain with { CatchAll = domain.CatchAll with { EvidenceExpiresAt = Now } },
            "future" => domain with { CatchAll = domain.CatchAll with { ObservedAt = Now.AddMinutes(1) } },
            "topology" => domain with { Dns = domain.Dns with { MxRecords = [new(10, "new.example.test")] } },
            "multiple_mx" => domain with { Dns = domain.Dns with { MxRecords = [new(10, "mx.example.test"), new(20, "mx2.example.test")] } },
            "provider" => domain with { Provider = new(MailProvider.Microsoft365, .9) },
            "strategy" => domain with { StrategyVersion = "old" },
            "weak" => domain with { CatchAll = domain.CatchAll with { Confidence = .2 } },
            "mixed" => domain with { CatchAll = domain.CatchAll with { ProbeResults = [Accepted(), Rejected()] } },
            "legacy" => domain with { CatchAll = domain.CatchAll with { EvidenceContractVersion = "legacy" } },
            "scope" => domain with { CatchAll = domain.CatchAll with { ControlScope = null } },
            "refresh_inconclusive" => domain with { CatchAll = domain.CatchAll with { RefreshInconclusive = true, RefreshAttemptedAt = Now } },
            "candidate" => domain with { CatchAll = domain.CatchAll with { ReasonCode = CatchAllReasonCode.AcceptAllCandidate, IndependentObservationCount = 1 } },
            _ => domain with { CatchAll = domain.CatchAll with { EvidenceExpiresAt = null } }
        };
        var plan = Plan(settings, domain);
        Assert.True(plan.PerformMailboxProbe);
        Assert.False(plan.UsePersistedNonDiscrimination);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StrongerConfiguredEvidenceThreshold_RequiresNewProbes(bool observations)
    {
        var settings = Settings();
        if (observations) settings.CatchAll.AcceptAllMinimumIndependentObservations = 3;
        else settings.CatchAll.MinimumAcceptedProbes = 3;
        Assert.True(Plan(settings, Domain(settings)).PerformMailboxProbe);
    }

    [Fact]
    public async Task ExpiredCapabilityWindow_RefreshesControlsAndMailbox_WithinRoutingTtl()
    {
        var settings = Settings();
        settings.ProviderCapabilities.Profiles["Generic"] = new() { NonDiscriminationRefreshMinutes = 5 };
        Approve(settings);
        var controls = new CountingControls();
        var smtp = new CountingSmtp();
        var result = await EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: smtp, catchAll: controls, cache: new Cache(Domain(settings)), clock: new Clock())
            .ValidateAsync("person@example.test", new(true));
        Assert.Equal(1, controls.Calls);
        Assert.Equal(1, smtp.Calls);
        Assert.DoesNotContain(ReasonCode.NonDiscriminationEvidenceReused, result.ReasonCodes);
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("contradiction")]
    public async Task FreshAttemptOrHistoricalContradiction_ForcesNewMailboxEvidence(string cause)
    {
        var settings = Settings();
        var observations = new InMemoryValidationObservationStore();
        var domain = Domain(settings);
        if (cause == "contradiction")
            await observations.RecordAsync(new("example.test", ValidationObservationType.MailboxProbe,
                MailProvider.GenericSmtp, "mx.example.test", CatchAllStatus.Unknown, .2,
                SmtpResponseCategory.RecipientRejected, Now.AddMinutes(-1), 1,
                TopologyFingerprint: domain.Provider.TopologyFingerprint, RecipientEvidenceQualified: true));
        var smtp = new CountingSmtp();
        var result = await EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: smtp, cache: new Cache(domain), clock: new Clock(), observationStore: observations)
            .ValidateAsync("person@example.test", new(true)
            { EvidenceObservedAfter = cause == "retry" ? Now.AddMinutes(-2) : null });
        Assert.Equal(1, smtp.Calls);
        Assert.Equal(Now, result.MailboxEvidenceObservedAt);
        Assert.DoesNotContain(ReasonCode.NonDiscriminationEvidenceReused, result.ReasonCodes);
    }

    [Theory]
    [InlineData(MailProvider.Yahoo, "yahoo.com")]
    [InlineData(MailProvider.Yahoo, "aol.com")]
    public async Task RestrictedProvider_RemainsInconclusive_AndDnsRetryRemainsAuthoritative(MailProvider provider, string domain)
    {
        var settings = Settings();
        var key = ProviderCapabilityPolicy.Key(provider, domain);
        settings.ProviderCapabilities.CanaryProviders = [key];
        settings.ProviderCapabilities.Profiles[key] = new() { ProbeMailbox = false, ProbeControls = false };
        Approve(settings);
        var smtp = new CountingSmtp();
        var controls = new CountingControls();
        var result = await EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: smtp, catchAll: controls, clock: new Clock())
            .ValidateAsync($"person@{domain}", new(true));
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(ReasonCode.ProviderCapabilityRestricted, result.ReasonCodes);
        Assert.Equal(0, smtp.Calls);
        Assert.Equal(0, controls.Calls);
        Assert.False(result.UnknownContext!.Retryable);
        var policy = new RevalidationPolicy(new ProviderPolicyResolver(Options.Create(settings)), Options.Create(settings));
        Assert.False(policy.Evaluate(result with { ReasonCodes = [ReasonCode.RetryRecommended] }, new(1)).ShouldRetry);
        var dnsFailure = result with { DomainIntelligence = result.DomainIntelligence! with
            { Dns = new(DnsStatus.Timeout, false, [], false, TimeSpan.Zero) } };
        Assert.True(policy.Evaluate(dnsFailure, new(1)).ShouldRetry);
    }

    [Fact]
    public void CauseSpecificRetryPolicy_DoesNotSuppressTransportRecovery()
    {
        var settings = Settings();
        settings.ProviderCapabilities.Profiles["Generic"] = new()
        { RetryVerificationBlocked = false, RetryAmbiguousAcceptance = false, MinimumRetrySeconds = 900 };
        Approve(settings);
        var options = Options.Create(settings);
        var resolver = new ProviderPolicyResolver(options);
        var policy = new RevalidationPolicy(resolver, options);
        var result = new EmailValidationResult { Email = "person@example.test", Checks = new(), Status = EmailValidationStatus.Unknown, MailProvider = MailProvider.GenericSmtp };
        foreach (var reason in new[] { ReasonCode.PolicyBlock, ReasonCode.MailboxAcceptanceAmbiguous })
            Assert.False(policy.Evaluate(result with { ReasonCodes = [reason, ReasonCode.RetryRecommended] }, new(1)).ShouldRetry);
        Assert.True(policy.Evaluate(result with { ReasonCodes = [ReasonCode.SmtpTimeout] }, new(1)).ShouldRetry);
        settings.Revalidation.MaximumPositiveJitterMilliseconds = 0;
        var schedule = new RevalidationSchedulePolicy(resolver, new DomainBackoffPolicy(options), options);
        Assert.Equal(Now.AddSeconds(900), schedule.CreateSchedule(new(result, ReasonCode.SmtpTimeout, 1, Now)).ScheduledAt);
        Assert.Equal(Now.AddSeconds(settings.Revalidation.MinimumRetrySeconds),
            schedule.CreateSchedule(new(result, ReasonCode.DnsTimeout, 1, Now)).ScheduledAt);
    }

    private static ValidationPlan Plan(EmailValidationOptions settings, DomainIntelligence domain) =>
        new ValidationPlanBuilder(Options.Create(settings)).Build(domain, true, true, ProviderCapabilityPolicy.PolicyVersions(settings), Now);

    internal static void Approve(EmailValidationOptions settings)
    {
        settings.ProviderCapabilities.ApprovalReference = "synthetic-fixture-review";
        settings.ProviderCapabilities.ApprovedPolicyHash = ProviderCapabilityPolicy.Fingerprint(settings.ProviderCapabilities);
    }

    private static EmailValidationOptions Settings()
    {
        var settings = new EmailValidationOptions();
        settings.Smtp.Enabled = true;
        settings.CatchAll.Enabled = true;
        settings.CatchAll.CacheMinutes = 120;
        settings.ProviderCapabilities.Mode = ProviderCapabilityMode.Enforced;
        settings.ProviderCapabilities.CanaryProviders = ["Generic"];
        settings.Revalidation.Enabled = true;
        settings.Smtp.RetryCount = 2;
        Approve(settings);
        return settings;
    }

    private static DomainIntelligence Domain(EmailValidationOptions settings)
    {
        var version = ProviderCapabilityPolicy.StrategyVersion(settings);
        var domain = new DomainIntelligence
        {
            Domain = "example.test", DomainExists = true,
            Dns = new(DnsStatus.Success, true, [new(10, "mx.example.test")], false, TimeSpan.Zero),
            Provider = new(MailProvider.GenericSmtp, .9, TopologyFingerprint: "10:mx.example.test"),
            ObservedAt = Now.AddMinutes(-10), EvidenceExpiresAt = Now.AddHours(2),
            RoutingEvidence = new(Now.AddMinutes(-10), Now.AddHours(2)),
            StrategyVersion = version, IntelligencePolicyVersion = settings.DomainIntelligence.PolicyVersion
        };
        return domain with { CatchAll = new(CatchAllStatus.Unknown, 2, 2, 0, 0, "Confirmed accept-all", .96)
        {
            RecipientBehavior = DomainRecipientBehavior.AcceptAll, ReasonCode = CatchAllReasonCode.AcceptAllConfirmed,
            IndependentObservationCount = 2, ObservedAt = Now.AddMinutes(-10), EvidenceExpiresAt = Now.AddHours(1),
            StrategyVersion = version, ProbeResults = [Accepted(), Accepted()],
            EvidenceContractVersion = CatchAllDetectionResult.CurrentRecipientBehaviorEvidenceContractVersion,
            ControlScope = new("mx.example.test", 10, MailProvider.GenericSmtp, domain.Provider.GatewayProvider,
                EndpointControlEvidencePolicy.TopologyFingerprint(domain), version, "synthetic-session")
        } };
    }

    private static SmtpProbeResult Accepted() => Probe(false, Now.AddMinutes(-10));
    private static SmtpProbeResult Rejected() => Probe(true, Now.AddMinutes(-10));
    private static SmtpProbeResult Probe(bool rejection, DateTimeOffset at)
    {
        var category = rejection ? SmtpResponseCategory.RecipientRejected : SmtpResponseCategory.Accepted;
        var code = rejection ? 550 : 250;
        var enhanced = rejection ? "5.1.1" : "2.1.5";
        return new(rejection ? SmtpMailboxStatus.Rejected : SmtpMailboxStatus.Accepted, code, "synthetic", TimeSpan.Zero,
            Evidence: new(SmtpCommand.RcptTo, code, enhanced, category, SmtpResponseTextClassification.Success,
                1, MailProvider.GenericSmtp, "mx.example.test", 1, at, "synthetic"),
            SessionEvidence: new(null,
                [new(SmtpCommand.MailFrom, 250, null, SmtpResponseCategory.Accepted, SmtpResponseTextClassification.Success, TimeSpan.Zero),
                 new(SmtpCommand.RcptTo, code, enhanced, category, SmtpResponseTextClassification.Success, TimeSpan.Zero)],
                "mx.example.test", TimeSpan.Zero, "probe@validator.test"));
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Routing : IDnsMailResolver
    {
        public Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnsLookupResult(DnsStatus.Success, true, [new(10, "mx.example.test")], false, TimeSpan.Zero));
    }
    private sealed class CountingSmtp(bool rejection = false) : ISmtpMailboxProbe
    {
        public Task<SmtpProbeResult> ProbeAsync(string mxHost, string recipient, CancellationToken cancellationToken = default) =>
            ProbeAsync(mxHost, recipient, MailProvider.Unknown, cancellationToken);
        public int Calls { get; private set; }
        public Task<SmtpProbeResult> ProbeAsync(string mxHost, string recipient, MailProvider provider = MailProvider.Unknown,
            CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Probe(rejection, Now)); }
    }
    private sealed class CountingControls : ICatchAllDetector
    {
        public int Calls { get; private set; }
        public Task<CatchAllDetectionResult> DetectAsync(string domain, string mxHost, MailProvider provider = MailProvider.Unknown,
            CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new CatchAllDetectionResult(CatchAllStatus.Unknown, 0, 0, 0, 0, "synthetic inconclusive")); }
    }
    private sealed class Cache(DomainIntelligence initial) : IDomainValidationCache
    {
        private DomainIntelligence _domain = initial;
        public int Count => 1;
        public bool TryGet(string domain, out DomainIntelligence? data) { data = _domain; return true; }
        public void Store(DomainIntelligence data, TimeSpan lifetime) => _domain = data;
        public Task<DomainIntelligence?> GetAsync(string domain, CancellationToken cancellationToken = default) => Task.FromResult<DomainIntelligence?>(_domain);
        public Task StoreAsync(DomainIntelligence data, TimeSpan lifetime, CancellationToken cancellationToken = default)
        { _domain = data; return Task.CompletedTask; }
    }
}
