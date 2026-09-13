using EmailValidation.Core;
using EmailValidation.Application;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class EmailValidatorTests
{
    private static readonly string[] SameDomainEmails =
        ["a@example.com", "b@example.com", "c@example.com"];

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
        Assert.Equal(UnknownCause.LiveVerificationDisabled, result.UnknownContext?.Cause);
        Assert.False(result.UnknownContext!.Retryable);
        Assert.Contains("Enable live SMTP", result.UnknownContext.RecommendedAction, StringComparison.Ordinal);
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
    public async Task FreshHighConfidenceCatchAll_IsReusedForSubsequentMailboxWithoutSmtpProbes()
    {
        var catchAll = new HighConfidenceCatchAll();
        var smtp = new CountingSmtp();
        var metrics = new ValidationPersistenceMetrics();
        var settings = LiveSettings();
        var validator = CreateValidator(
            new FakeDns(), settings, catchAll: catchAll, smtp: smtp, metrics: metrics);

        var first = await validator.ValidateAsync(
            "one@example.com", new EmailValidationRequest(EnableSmtp: true));
        var second = await validator.ValidateAsync(
            "two@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(1, catchAll.Calls);
        Assert.Equal(1, smtp.Calls);
        Assert.Equal(EmailValidationStatus.CatchAll, second.Status);
        Assert.NotEqual(EmailValidationStatus.Valid, second.Status);
        Assert.Equal(SmtpMailboxStatus.NotAttempted, second.Checks.Mailbox);
        Assert.Equal(ValidationResultSource.PersistentDomainIntelligence, second.Metadata!.ResultSource);
        Assert.True(second.Diagnostics?.UsedPersistedCatchAll);
        Assert.True(second.Diagnostics?.MailboxProbeSkippedDueToCatchAll);
        Assert.Equal(first.CatchAllEvidence!.ObservedAt, second.CatchAllEvidence!.ObservedAt);
        Assert.Contains("Persisted domain evidence", second.ConfidenceReason, StringComparison.Ordinal);
        Assert.Equal(1, metrics.GetSnapshot().CatchAllLiveProbesAvoided);
        Assert.Equal(1, metrics.GetSnapshot().MailboxProbesAvoidedDueToCatchAll);
    }

    [Fact]
    public async Task SameDomainBatch_DiscoversCatchAllOnceAndReusesItImmediately()
    {
        var catchAll = new HighConfidenceCatchAll();
        var smtp = new CountingSmtp();
        var validator = CreateValidator(
            new FakeDns(), LiveSettings(), catchAll: catchAll, smtp: smtp);

        var results = new List<EmailValidationResult>();
        foreach (var email in new[] { "a@example.com", "b@example.com", "c@example.com" })
            results.Add(await validator.ValidateAsync(email, new EmailValidationRequest(EnableSmtp: true)));

        Assert.Equal(1, catchAll.Calls);
        Assert.Equal(1, smtp.Calls);
        Assert.Equal(ValidationResultSource.LiveValidation, results[0].Metadata!.ResultSource);
        Assert.All(results.Skip(1), result =>
            Assert.Equal(ValidationResultSource.PersistentDomainIntelligence, result.Metadata!.ResultSource));
    }

    [Fact]
    public async Task WeakCatchAllEvidence_DoesNotSuppressLiveMailboxValidation()
    {
        var catchAll = new WeakCatchAll();
        var smtp = new CountingSmtp();
        var validator = CreateValidator(
            new FakeDns(), LiveSettings(), catchAll: catchAll, smtp: smtp);

        await validator.ValidateAsync("one@example.com", new EmailValidationRequest(EnableSmtp: true));
        var second = await validator.ValidateAsync(
            "two@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(2, catchAll.Calls);
        Assert.Equal(2, smtp.Calls);
        Assert.True(second.ProbeAttempted);
        Assert.False(second.Diagnostics?.UsedPersistedCatchAll);
    }

    [Fact]
    public async Task PersistedCatchAllLoadedByNewCacheInstance_SkipsDnsCatchAllAndMailboxProbes()
    {
        var dns = new FakeDns();
        var catchAll = new HighConfidenceCatchAll();
        var smtp = new CountingSmtp();
        var cache = new HistoricalDomainCache(CachedCatchAllDomain());
        var validator = CreateValidator(
            dns, LiveSettings(), catchAll: catchAll, smtp: smtp, cache: cache);

        var result = await validator.ValidateAsync(
            "after-restart@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(0, dns.Calls);
        Assert.Equal(0, catchAll.Calls);
        Assert.Equal(0, smtp.Calls);
        Assert.Equal(ValidationResultSource.PersistentDomainIntelligence, result.Metadata!.ResultSource);
        Assert.Equal(CatchAllReasonCode.IndependentRoutingEvidence, result.CatchAllEvidence!.ReasonCode);
    }

    [Fact]
    public async Task MxTopologyChange_InvalidatesPersistedCatchAllAndRevalidatesDomain()
    {
        var dns = new FakeDns();
        var catchAll = new HighConfidenceCatchAll();
        var smtp = new CountingSmtp();
        var stale = CachedCatchAllDomain() with
        {
            Provider = CachedCatchAllDomain().Provider with { TopologyFingerprint = "0:old-mx.example.com" },
            EvidenceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var validator = CreateValidator(
            dns, LiveSettings(), catchAll: catchAll, smtp: smtp, cache: new HistoricalDomainCache(stale));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true));

        Assert.Equal(1, dns.Calls);
        Assert.Equal(1, catchAll.Calls);
        Assert.Equal(1, smtp.Calls);
        Assert.Equal("10:mx.example.com", result.Provider!.TopologyFingerprint);
        Assert.Equal(ValidationResultSource.LiveValidation, result.Metadata!.ResultSource);
    }

    [Fact]
    public async Task InconclusiveCatchAllRefresh_PreservesHistoryAndBacksOffFurtherRandomProbes()
    {
        var catchAll = new InconclusiveCatchAll();
        var smtp = new CountingSmtp();
        var stale = CachedCatchAllDomain() with
        {
            CatchAll = CachedCatchAllDomain().CatchAll with { ObservedAt = DateTimeOffset.UtcNow.AddDays(-2) },
            EvidenceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var cache = new HistoricalDomainCache(stale);
        var validator = CreateValidator(
            new FakeDns(), LiveSettings(), catchAll: catchAll, smtp: smtp, cache: cache);

        var first = await validator.ValidateAsync(
            "one@example.com", new EmailValidationRequest(EnableSmtp: true));
        await validator.ValidateAsync("two@example.com", new EmailValidationRequest(EnableSmtp: true));

        Assert.Equal(1, catchAll.Calls);
        Assert.Equal(2, smtp.Calls);
        Assert.Equal(CatchAllStatus.LikelyCatchAll, first.CatchAllEvidence!.Status);
        Assert.True(first.CatchAllEvidence.RefreshInconclusive);
        Assert.Contains("refresh was inconclusive", first.CatchAllEvidence.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptedTargetWithOnlyAmbiguousControls_IsUnknownAndRetryable()
    {
        var validator = CreateValidator(
            new FakeDns(), LiveSettings(), catchAll: new InconclusiveCatchAll(), smtp: new CountingSmtp());

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Equal(EvidenceQuality.Partial, result.EvidenceQuality);
        Assert.Equal(UnknownCause.InsufficientEvidence, result.UnknownContext?.Cause);
        Assert.True(result.UnknownContext?.Retryable);
        Assert.Contains(ReasonCode.MailboxAcceptanceAmbiguous, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.MailboxAccepted, result.ReasonCodes);
    }

    [Fact]
    public async Task ContradictoryRandomRejections_ReplaceOldCatchAllClassification()
    {
        var stale = CachedCatchAllDomain() with
        {
            CatchAll = CachedCatchAllDomain().CatchAll with { ObservedAt = DateTimeOffset.UtcNow.AddDays(-2) },
            EvidenceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var validator = CreateValidator(
            new FakeDns(), LiveSettings(), catchAll: new CountingCatchAll(),
            smtp: new CountingSmtp(), cache: new HistoricalDomainCache(stale));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true));

        Assert.Equal(CatchAllStatus.LikelyNotCatchAll, result.CatchAllEvidence!.Status);
        Assert.Equal(CatchAllReasonCode.None, result.CatchAllEvidence.ReasonCode);
    }

    [Fact]
    public async Task ConcurrentStaleDomainRequests_ShareOneCatchAllRefresh()
    {
        var catchAll = new BlockingCatchAll();
        var smtp = new CountingSmtp();
        var stale = CachedCatchAllDomain() with
        {
            CatchAll = CachedCatchAllDomain().CatchAll with { ObservedAt = DateTimeOffset.UtcNow.AddDays(-2) },
            EvidenceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var validator = CreateValidator(
            new FakeDns(), LiveSettings(), catchAll: catchAll, smtp: smtp,
            cache: new HistoricalDomainCache(stale));

        var validations = SameDomainEmails
            .Select(email => validator.ValidateAsync(email, new EmailValidationRequest(EnableSmtp: true)))
            .ToArray();
        await catchAll.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        catchAll.Release.SetResult();
        var results = await Task.WhenAll(validations);

        Assert.Equal(1, catchAll.Calls);
        Assert.Equal(1, smtp.Calls);
        Assert.Single(results, result => result.Metadata!.ResultSource == ValidationResultSource.LiveValidation);
        Assert.Equal(2, results.Count(result =>
            result.Metadata!.ResultSource == ValidationResultSource.PersistentDomainIntelligence));
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
        var observations = new InMemoryValidationObservationStore();
        var smtp = new MxSequenceSmtp(new Dictionary<string, SmtpProbeResult>
        {
            ["mx1.example.com"] = RecipientAccepted("mx1.example.com"),
            ["mx2.example.com"] = RecipientRejected("mx2.example.com")
        });
        var validator = CreateValidator(
            new MultiMxDns(), LiveSettings(), observations, smtp: smtp,
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Equal(MxConsensus.Conflicting, result.MxValidation?.Consensus);
        Assert.Contains(ReasonCode.MxResultsConflicting, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.MailboxRejected, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.MicrosoftRecipientRejected, result.ReasonCodes);
        Assert.Equal(DetailedStatus.ConflictingMxEvidence, result.SubStatus);
        Assert.Equal(UnknownCause.ConflictingMxEvidence, result.UnknownContext?.Cause);
        Assert.Equal(EvidenceQuality.Partial, result.EvidenceQuality);
        Assert.Contains("conflicting", result.ConfidenceReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VerificationReliabilityLevel.Low, result.ProviderValidation?.VerificationReliabilityLevel);
        var mailboxObservation = Assert.Single(
            await observations.GetDomainObservationsAsync("example.com"),
            observation => observation.Type == ValidationObservationType.MailboxProbe);
        Assert.Equal(SmtpResponseCategory.RecipientRejected, mailboxObservation.ResponseCategory);
        Assert.True(mailboxObservation.RecipientEvidenceQualified);
        Assert.True(mailboxObservation.RecipientEvidenceContested);
        var history = new HistoricalSignalAggregator().Aggregate(
            await observations.GetDomainObservationsAsync("example.com"));
        Assert.Equal(0, history.TargetRejectedCount);
        Assert.Equal(0, history.RecipientRejectionRate);
    }

    [Fact]
    public async Task TwoCorrelatedRecordedSessions_ConfirmAcceptAllWithoutCallingItCatchAll()
    {
        var settings = LiveSettings();
        settings.CatchAll.ProbeCount = 2;
        settings.CatchAll.MaxProbeCount = 2;
        settings.CatchAll.MinimumAcceptedProbes = 2;
        settings.CatchAll.AcceptAllMinimumIndependentObservations = 2;
        settings.CatchAll.AcceptAllMinimumObservationSeparationMinutes = 15;
        settings.CatchAll.AcceptAllSessionCorrelationMinutes = 5;
        var observations = new InMemoryValidationObservationStore();
        var firstAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        var first = CreateValidator(
            new FakeDns(), settings, observations,
            new TimedCandidateCatchAll(firstAt),
            new TimedAcceptedSmtp(firstAt.AddSeconds(10)));

        var firstResult = await first.ValidateAsync(
            "first@example.com", new EmailValidationRequest(EnableSmtp: true));
        var recorded = await observations.GetDomainObservationsAsync("example.com");
        var firstControl = Assert.Single(recorded, item => item.Type == ValidationObservationType.CatchAllProbe);
        var firstTarget = Assert.Single(recorded, item => item.Type == ValidationObservationType.MailboxProbe);

        Assert.Equal(CatchAllReasonCode.AcceptAllCandidate, firstResult.CatchAllEvidence?.ReasonCode);
        Assert.Equal(1, firstResult.CatchAllEvidence?.IndependentObservationCount);
        Assert.Equal(EvidenceQuality.Partial, firstResult.EvidenceQuality);
        Assert.Equal(DetailedStatus.AcceptAllCandidate, firstResult.SubStatus);
        Assert.Equal(UnknownCause.AcceptAllPendingConfirmation, firstResult.UnknownContext?.Cause);
        Assert.True(firstResult.UnknownContext?.Retryable);
        Assert.NotNull(firstResult.RetryAfter);
        Assert.DoesNotContain(ReasonCode.MailboxAccepted, firstResult.ReasonCodes);
        Assert.DoesNotContain(DetailedStatus.MailboxAccepted, firstResult.DetailedStatuses);
        Assert.Equal(2, firstControl.RandomRecipientAcceptedCount);
        Assert.Equal(2, firstControl.RandomRecipientProbeCount);
        Assert.False(string.IsNullOrWhiteSpace(firstControl.ObservationSessionId));
        Assert.Equal(firstControl.ObservationSessionId, firstTarget.ObservationSessionId);
        Assert.Equal(SmtpResponseCategory.Accepted, firstControl.CorrelatedTargetResponseCategory);
        Assert.True(firstControl.CorrelatedTargetRecipientEvidenceQualified);
        Assert.Equal("mx.example.com", firstControl.CorrelatedTargetMxHost);

        var secondAt = firstAt.AddMinutes(16);
        var second = CreateValidator(
            new FakeDns(), settings, observations,
            new TimedCandidateCatchAll(secondAt),
            new TimedAcceptedSmtp(secondAt.AddSeconds(10)));
        var secondResult = await second.ValidateAsync(
            "second@example.com", new EmailValidationRequest(EnableSmtp: true));

        Assert.Equal(EmailValidationStatus.Unknown, secondResult.Status);
        Assert.Equal(DomainRecipientBehavior.AcceptAll,
            secondResult.CatchAllEvidence?.EffectiveRecipientBehavior);
        Assert.Equal(CatchAllReasonCode.AcceptAllConfirmed, secondResult.CatchAllEvidence?.ReasonCode);
        Assert.Equal(2, secondResult.CatchAllEvidence?.IndependentObservationCount);
        Assert.NotEqual(DomainRecipientBehavior.CatchAll,
            secondResult.CatchAllEvidence?.EffectiveRecipientBehavior);
        Assert.Equal(EvidenceQuality.Partial, secondResult.EvidenceQuality);
        Assert.Equal(DetailedStatus.AcceptAllConfirmed, secondResult.SubStatus);
        Assert.Equal(UnknownCause.NonDiscriminatingSmtpEndpoint, secondResult.UnknownContext?.Cause);
        Assert.False(secondResult.UnknownContext?.Retryable);
        Assert.Equal(secondResult.CatchAllEvidence?.Status, secondResult.Checks.CatchAll);
        Assert.DoesNotContain(DetailedStatus.MailboxAccepted, secondResult.DetailedStatuses);
    }

    [Fact]
    public async Task MixedControlMxHosts_ArePersistedWithoutInventedMxProvenance()
    {
        var settings = LiveSettings();
        settings.CatchAll.ProbeCount = 2;
        settings.CatchAll.MaxProbeCount = 2;
        settings.CatchAll.MinimumAcceptedProbes = 2;
        var observations = new InMemoryValidationObservationStore();
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var validator = CreateValidator(
            new FakeDns(), settings, observations,
            new MixedMxCandidateCatchAll(observedAt),
            new TimedAcceptedSmtp(observedAt.AddSeconds(10)));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true));
        var recorded = await observations.GetDomainObservationsAsync("example.com");
        var control = Assert.Single(recorded, item => item.Type == ValidationObservationType.CatchAllProbe);

        Assert.Equal(CatchAllReasonCode.AcceptAllCandidate, result.CatchAllEvidence?.ReasonCode);
        Assert.Equal(0, result.CatchAllEvidence?.IndependentObservationCount);
        Assert.Null(control.MxHost);
        Assert.Equal(2, control.RandomRecipientAcceptedCount);
    }

    [Fact]
    public async Task AcceptedTargetWithAmbiguousMxPeer_IsNotPersistedAsQualifyingSession()
    {
        var settings = LiveSettings();
        settings.CatchAll.ProbeCount = 2;
        settings.CatchAll.MaxProbeCount = 2;
        settings.CatchAll.MinimumAcceptedProbes = 2;
        settings.Smtp.MaxMxAttempts = 2;
        var observations = new InMemoryValidationObservationStore();
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var smtp = new MxSequenceSmtp(new Dictionary<string, SmtpProbeResult>
        {
            ["mx1.example.com"] = RecipientAccepted("mx1.example.com", observedAt.AddSeconds(10)),
            ["mx2.example.com"] = TemporaryFailure("mx2.example.com")
        });
        var validator = CreateValidator(
            new MultiMxDns(), settings, observations,
            new TimedCandidateCatchAll(observedAt), smtp);

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true));
        var recorded = await observations.GetDomainObservationsAsync("example.com");
        var control = Assert.Single(recorded,
            observation => observation.Type == ValidationObservationType.CatchAllProbe);
        var target = Assert.Single(recorded,
            observation => observation.Type == ValidationObservationType.MailboxProbe);

        Assert.Equal(0, result.CatchAllEvidence?.IndependentObservationCount);
        Assert.False(control.CorrelatedTargetRecipientEvidenceQualified);
        Assert.False(target.RecipientEvidenceQualified);
    }

    [Fact]
    public async Task SmtpBannerProvider_ReconcilesGenericMxBeforeMailboxInterpretation()
    {
        var validator = CreateValidator(
            new FakeDns(),
            LiveSettings(),
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown),
            smtp: new BannerAcceptedSmtp(
                "220 tenant.mail.protection.outlook.com Microsoft ESMTP"));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true));

        Assert.Equal(MailProvider.Microsoft365, result.MailProvider);
        Assert.Equal(MailProvider.Microsoft365, result.Provider?.Provider);
        Assert.Equal(MailProvider.GenericSmtp, result.DomainIntelligence?.Provider.Provider);
        Assert.Equal(SmtpResponseCategory.GatewayAccepted,
            result.ProviderValidation?.EffectiveCategory);
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(ReasonCode.MailboxAcceptanceAmbiguous, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.MailboxAccepted, result.ReasonCodes);
    }

    [Fact]
    public async Task ConflictingDnsAndSmtpProviderEvidence_Abstains()
    {
        var validator = CreateValidator(
            new MicrosoftDns(),
            LiveSettings(),
            catchAll: new StaticCatchAll(CatchAllStatus.NotCatchAll),
            smtp: new BannerAcceptedSmtp("220 mx.google.com ESMTP"));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true));

        Assert.Equal(MailProvider.Unknown, result.MailProvider);
        Assert.Equal(MailProvider.Unknown, result.Provider?.Provider);
        Assert.Contains("ProviderEvidenceConflict", result.Provider?.Evidence ?? []);
        Assert.Equal(MailProvider.Microsoft365, result.DomainIntelligence?.Provider.Provider);
        Assert.DoesNotContain(
            "ProviderEvidenceConflict",
            result.DomainIntelligence?.Provider.Evidence ?? []);
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Contains(ReasonCode.ProviderEvidenceConflicting, result.ReasonCodes);
        Assert.Equal(DetailedStatus.ConflictingProviderEvidence, result.SubStatus);
        Assert.Equal(UnknownCause.ConflictingProviderEvidence, result.UnknownContext?.Cause);
        Assert.Equal(EvidenceQuality.Partial, result.EvidenceQuality);
    }

    [Fact]
    public async Task MicrosoftConsumerDnsAndMicrosoftBanner_AreCompatible()
    {
        var validator = CreateValidator(
            new MicrosoftConsumerDns(),
            LiveSettings(),
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown),
            smtp: new BannerAcceptedSmtp(
                "220 outlook-com.olc.protection.outlook.com Microsoft ESMTP"));

        var result = await validator.ValidateAsync(
            "person@outlook.com", new EmailValidationRequest(EnableSmtp: true));

        Assert.Equal(MailProvider.MicrosoftConsumer, result.MailProvider);
        Assert.DoesNotContain(ReasonCode.ProviderEvidenceConflicting, result.ReasonCodes);
        Assert.Equal(SmtpResponseCategory.GatewayAccepted,
            result.ProviderValidation?.EffectiveCategory);
    }

    [Fact]
    public async Task ProviderIdentityConflict_DoesNotEraseStrongRecipientRejection()
    {
        var validator = CreateValidator(
            new MicrosoftDns(),
            LiveSettings(),
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown),
            smtp: new BannerRejectedSmtp("220 mx.google.com ESMTP"));

        var result = await validator.ValidateAsync(
            "missing@example.com", new EmailValidationRequest(EnableSmtp: true));

        Assert.Equal(EmailValidationStatus.Invalid, result.Status);
        Assert.Contains(ReasonCode.MailboxRejected, result.ReasonCodes);
        Assert.Contains(ReasonCode.ProviderEvidenceConflicting, result.ReasonCodes);
        Assert.Equal(EvidenceQuality.Conclusive, result.EvidenceQuality);
        Assert.NotEqual(DetailedStatus.ConflictingProviderEvidence, result.SubStatus);
    }

    [Fact]
    public async Task ProviderIdentityConflict_DoesNotEraseStageQualifiedMailboxFull()
    {
        var validator = CreateValidator(
            new MicrosoftDns(),
            LiveSettings(),
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown),
            smtp: new BannerMailboxFullSmtp("220 mx.google.com ESMTP"));

        var result = await validator.ValidateAsync(
            "full@example.com", new EmailValidationRequest(EnableSmtp: true));

        Assert.Equal(EmailValidationStatus.Risky, result.Status);
        Assert.Equal(SmtpResponseCategory.MailboxFull,
            result.ProviderValidation?.EffectiveCategory);
        Assert.Contains(ReasonCode.ProviderEvidenceConflicting, result.ReasonCodes);
        Assert.Equal(EvidenceQuality.Conclusive, result.EvidenceQuality);
    }

    [Fact]
    public async Task EqualPreferenceMxPeers_AreExhaustedBeforeDeclaringRecipientOutcome()
    {
        var smtp = new MxSequenceSmtp(new Dictionary<string, SmtpProbeResult>
        {
            ["mx1.example.com"] = RecipientRejected("mx1.example.com"),
            ["mx2.example.com"] = RecipientAccepted("mx2.example.com")
        });
        var validator = CreateValidator(
            new EqualPreferenceMxDns(), LiveSettings(), smtp: smtp,
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Equal(MxConsensus.Conflicting, result.MxValidation?.Consensus);
        Assert.Equal(["mx1.example.com", "mx2.example.com"], result.MxValidation?.HostsAttempted);
        Assert.True(result.RecipientEvidence?.Qualified);
        Assert.True(result.RecipientEvidence?.Contested);
        Assert.Equal(SmtpResponseCategory.RecipientRejected, result.RecipientEvidence?.Category);
        Assert.Equal(SmtpCommand.RcptTo, result.RecipientEvidence?.Stage);
    }

    [Fact]
    public async Task EqualPreferenceAcceptedAndMailboxFull_PreservesCurrentDeliveryRisk()
    {
        var smtp = new MxSequenceSmtp(new Dictionary<string, SmtpProbeResult>
        {
            ["mx1.example.com"] = RecipientAccepted("mx1.example.com"),
            ["mx2.example.com"] = MailboxFull("mx2.example.com")
        });
        var validator = CreateValidator(
            new EqualPreferenceMxDns(), LiveSettings(), smtp: smtp,
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown));

        var result = await validator.ValidateAsync(
            "full@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(EmailValidationStatus.Risky, result.Status);
        Assert.Equal(SmtpResponseCategory.MailboxFull,
            result.ProviderValidation?.EffectiveCategory);
        Assert.Equal(MxConsensus.ConclusivePositive, result.MxValidation?.Consensus);
        Assert.Equal("mx2.example.com", result.SelectedMx);
        Assert.Equal(["mx1.example.com", "mx2.example.com"], result.MxValidation?.HostsAttempted);
    }

    [Fact]
    public async Task EqualPreferenceRejectedAndMailboxFull_AreConflicting()
    {
        var smtp = new MxSequenceSmtp(new Dictionary<string, SmtpProbeResult>
        {
            ["mx1.example.com"] = RecipientRejected("mx1.example.com"),
            ["mx2.example.com"] = MailboxFull("mx2.example.com")
        });
        var validator = CreateValidator(
            new EqualPreferenceMxDns(), LiveSettings(), smtp: smtp,
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Equal(MxConsensus.Conflicting, result.MxValidation?.Consensus);
        Assert.Contains(ReasonCode.MxResultsConflicting, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.MailboxRejected, result.ReasonCodes);
        Assert.Equal(EvidenceQuality.Partial, result.EvidenceQuality);
        Assert.Equal(["mx1.example.com", "mx2.example.com"], result.MxValidation?.HostsAttempted);
    }

    [Fact]
    public async Task UnqualifiedRawMailboxFull_DoesNotStopBeforeLowerPriorityConclusiveResult()
    {
        var smtp = new MxSequenceSmtp(new Dictionary<string, SmtpProbeResult>
        {
            ["mx1.example.com"] = RawMailboxFull("mx1.example.com"),
            ["mx2.example.com"] = RecipientRejected("mx2.example.com"),
            ["mx3.example.com"] = RecipientAccepted("mx3.example.com")
        });
        var validator = CreateValidator(
            new MultiMxDns(), LiveSettings(), smtp: smtp,
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown));

        var result = await validator.ValidateAsync(
            "missing@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.Equal(EmailValidationStatus.Invalid, result.Status);
        Assert.Equal(MxConsensus.ConclusiveNegative, result.MxValidation?.Consensus);
        Assert.Equal("mx2.example.com", result.SelectedMx);
        Assert.Equal(["mx1.example.com", "mx2.example.com"], result.MxValidation?.HostsAttempted);
    }

    [Fact]
    public async Task TemporaryPrimaryMx_FollowedByLocalCooldown_PreservesAttemptedEvidence()
    {
        var smtp = new MxSequenceSmtp(new Dictionary<string, SmtpProbeResult>
        {
            ["mx1.example.com"] = TemporaryFailure("mx1.example.com"),
            ["mx2.example.com"] = LocalCooldown("mx2.example.com")
        });
        var settings = LiveSettings();
        settings.Smtp.MaxMxAttempts = 2;
        var validator = CreateValidator(
            new MultiMxDns(), settings, smtp: smtp,
            catchAll: new StaticCatchAll(CatchAllStatus.Unknown));

        var result = await validator.ValidateAsync(
            "person@example.com", new EmailValidationRequest(EnableSmtp: true, Verbose: true));

        Assert.True(result.ProbeAttempted);
        Assert.Equal(SmtpProbeDisposition.Completed, result.ProbeDisposition);
        Assert.Equal("mx1.example.com", result.SelectedMx);
        Assert.Equal(SmtpResponseCategory.TemporaryFailure, result.ProviderValidation?.EffectiveCategory);
        Assert.Equal(["mx1.example.com", "mx2.example.com"], result.MxValidation?.HostsAttempted);
    }

    private static EmailValidator CreateValidator(
        IDnsMailResolver dns,
        EmailValidationOptions? settings = null,
        IValidationObservationStore? observationStore = null,
        ICatchAllDetector? catchAll = null,
        ISmtpMailboxProbe? smtp = null,
        IDomainValidationCache? cache = null,
        IValidationPersistenceMetrics? metrics = null)
    {
        settings ??= new EmailValidationOptions();
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var persistenceMetrics = metrics ?? new ValidationPersistenceMetrics();
        var domainCache = cache ?? new InMemoryDomainValidationCache();
        var catchAllDetector = catchAll ?? new FakeCatchAll();
        var providerDetector = new MailProviderDetector();
        var planBuilder = new ValidationPlanBuilder(options);
        var domainService = new DomainIntelligenceService(
            new MailRoutingAnalyzer(dns),
            new UnknownDnsSecurityAnalyzer(),
            new UnknownAuthenticationAnalyzer(),
            new UnknownDisposableProvider(),
            new FakeDomainIntelligence(),
            providerDetector,
            catchAllDetector,
            domainCache,
            planBuilder,
            new DomainIntelligenceFreshnessPolicy(options),
            persistenceMetrics,
            options,
            TimeProvider.System,
            NullLogger<DomainIntelligenceService>.Instance);
        IMailProviderStrategy[] strategies =
        [
            new Microsoft365Strategy(), new GoogleWorkspaceStrategy(), new ProofpointStrategy(),
            new MimecastStrategy(), new GenericSmtpStrategy()
        ];
        return new EmailValidator(
            new EmailNormalizer(), new FakeEmailIntelligence(), new RoleAccountDetector(options),
            smtp ?? new FakeSmtp(), new HealthyProbeSender(),
            new EmailClassificationEngine(), new MailProviderStrategyResolver(strategies),
            observationStore ?? new InMemoryValidationObservationStore(), new HistoricalSignalAggregator(),
            new ResultEvaluator(), new SmtpSessionBudget(), persistenceMetrics,
            domainService, new SmtpBannerProviderDetector(), options, NullLogger<EmailValidator>.Instance);
    }

    private sealed class UnknownDnsSecurityAnalyzer : IDnsSecurityAnalyzer
    {
        public Task<DnsSecurityIntelligence> AnalyzeAsync(
            string domain,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DnsSecurityIntelligence.Unknown);
    }

    private sealed class UnknownAuthenticationAnalyzer : IEmailAuthenticationAnalyzer
    {
        public Task<EmailAuthenticationIntelligence> AnalyzeAsync(
            string domain,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailAuthenticationIntelligence.Unknown);
    }

    private sealed class UnknownDisposableProvider : IDisposableEmailDomainProvider
    {
        public ValueTask<DisposableDomainResult> GetAsync(
            string domain,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DisposableDomainResult.Unknown);
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

    private sealed class MicrosoftConsumerDns : IDnsMailResolver
    {
        public Task<DnsLookupResult> ResolveAsync(
            string domain,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnsLookupResult(
                DnsStatus.Success, true,
                [new MxRecord(0, "outlook-com.olc.protection.outlook.com")],
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

    private sealed class EqualPreferenceMxDns : IDnsMailResolver
    {
        public Task<DnsLookupResult> ResolveAsync(
            string domain,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnsLookupResult(
                DnsStatus.Success, true,
                [new MxRecord(10, "mx1.example.com"), new MxRecord(10, "mx2.example.com")],
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
            Task.FromResult(RecipientAccepted(mxHost));
    }

    private sealed class CountingSmtp : ISmtpMailboxProbe
    {
        public int Calls { get; private set; }

        public Task<SmtpProbeResult> ProbeAsync(
            string mxHost,
            string recipient,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(RecipientAccepted(mxHost));
        }
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

    private sealed class TimedCandidateCatchAll(DateTimeOffset observedAt) : ICatchAllDetector
    {
        public Task<CatchAllDetectionResult> DetectAsync(
            string domain,
            string mxHost,
            MailProvider provider,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new CatchAllDetectionResult(
                CatchAllStatus.Unknown, 2, 2, 0, 0,
                "The endpoint accepted two randomized recipients; independent confirmation is required.",
                0.71)
            {
                ReasonCode = CatchAllReasonCode.AcceptAllCandidate,
                IndependentObservationCount = 0,
                ObservedAt = observedAt,
                RefreshAttemptedAt = observedAt,
                RefreshInconclusive = true,
                ProbeResults =
                [
                    RecipientAccepted(mxHost, observedAt.AddSeconds(-1)),
                    RecipientAccepted(mxHost, observedAt)
                ]
            });
    }

    private sealed class TimedAcceptedSmtp(DateTimeOffset observedAt) : ISmtpMailboxProbe
    {
        public Task<SmtpProbeResult> ProbeAsync(
            string mxHost,
            string recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RecipientAccepted(mxHost, observedAt));
    }

    private sealed class BannerAcceptedSmtp(string banner) : ISmtpMailboxProbe
    {
        public Task<SmtpProbeResult> ProbeAsync(
            string mxHost,
            string recipient,
            CancellationToken cancellationToken = default)
        {
            var accepted = RecipientAccepted(mxHost);
            return Task.FromResult(accepted with
            {
                SessionEvidence = accepted.SessionEvidence! with { ServerBanner = banner }
            });
        }
    }

    private sealed class BannerRejectedSmtp(string banner) : ISmtpMailboxProbe
    {
        public Task<SmtpProbeResult> ProbeAsync(
            string mxHost,
            string recipient,
            CancellationToken cancellationToken = default)
        {
            var rejected = RecipientRejected(mxHost);
            return Task.FromResult(rejected with
            {
                SessionEvidence = rejected.SessionEvidence! with { ServerBanner = banner }
            });
        }
    }

    private sealed class BannerMailboxFullSmtp(string banner) : ISmtpMailboxProbe
    {
        public Task<SmtpProbeResult> ProbeAsync(
            string mxHost,
            string recipient,
            CancellationToken cancellationToken = default)
        {
            var full = MailboxFull(mxHost);
            return Task.FromResult(full with
            {
                SessionEvidence = full.SessionEvidence! with { ServerBanner = banner }
            });
        }
    }

    private sealed class MixedMxCandidateCatchAll(DateTimeOffset observedAt) : ICatchAllDetector
    {
        public Task<CatchAllDetectionResult> DetectAsync(
            string domain,
            string mxHost,
            MailProvider provider,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new CatchAllDetectionResult(
                CatchAllStatus.Unknown, 2, 2, 0, 0,
                "The endpoint accepted controls on different MX hosts.",
                0.60)
            {
                ReasonCode = CatchAllReasonCode.AcceptAllCandidate,
                ObservedAt = observedAt,
                RefreshAttemptedAt = observedAt,
                RefreshInconclusive = true,
                ProbeResults =
                [
                    RecipientAccepted(mxHost, observedAt.AddSeconds(-1)),
                    RecipientAccepted("mx2.example.com", observedAt)
                ]
            });
    }

    private sealed class HighConfidenceCatchAll : ICatchAllDetector
    {
        public int Calls { get; private set; }

        public Task<CatchAllDetectionResult> DetectAsync(
            string domain, string mxHost, MailProvider provider,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CatchAllDetectionResult(
                CatchAllStatus.LikelyCatchAll, 2, 2, 0, 0,
                "Independent routing evidence confirms otherwise nonexistent recipients are routed.", 0.96)
            {
                ReasonCode = CatchAllReasonCode.IndependentRoutingEvidence,
                RecipientBehavior = DomainRecipientBehavior.CatchAll
            });
        }
    }

    private sealed class WeakCatchAll : ICatchAllDetector
    {
        public int Calls { get; private set; }

        public Task<CatchAllDetectionResult> DetectAsync(
            string domain, string mxHost, MailProvider provider,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CatchAllDetectionResult(
                CatchAllStatus.LikelyCatchAll, 1, 1, 0, 0,
                "Weak independent routing evidence indicates catch-all delivery.", 0.70)
            {
                ReasonCode = CatchAllReasonCode.IndependentRoutingEvidence,
                RecipientBehavior = DomainRecipientBehavior.CatchAll
            });
        }
    }

    private sealed class InconclusiveCatchAll : ICatchAllDetector
    {
        public int Calls { get; private set; }

        public Task<CatchAllDetectionResult> DetectAsync(
            string domain, string mxHost, MailProvider provider,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CatchAllDetectionResult(
                CatchAllStatus.Unknown, 1, 0, 0, 1,
                "The provider timed out during refresh.", 0.20)
            {
                ReasonCode = CatchAllReasonCode.MixedOrInconclusive,
                RefreshInconclusive = true
            });
        }
    }

    private sealed class BlockingCatchAll : ICatchAllDetector
    {
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CatchAllDetectionResult> DetectAsync(
            string domain, string mxHost, MailProvider provider,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new CatchAllDetectionResult(
                CatchAllStatus.LikelyCatchAll, 2, 2, 0, 0,
                "Independent routing evidence confirms otherwise nonexistent recipients are routed.", 0.96)
            {
                ReasonCode = CatchAllReasonCode.IndependentRoutingEvidence,
                RecipientBehavior = DomainRecipientBehavior.CatchAll
            };
        }
    }

    private sealed class HistoricalDomainCache(DomainIntelligence initial) : IDomainValidationCache
    {
        private DomainIntelligence _domain = initial;

        public int Count => 1;

        public bool TryGet(string domain, out DomainIntelligence? data)
        {
            data = _domain;
            return true;
        }

        public void Store(DomainIntelligence data, TimeSpan lifetime) => _domain = data;

        public Task<DomainIntelligence?> GetAsync(
            string domain,
            CancellationToken cancellationToken = default) => Task.FromResult<DomainIntelligence?>(_domain);

        public Task StoreAsync(
            DomainIntelligence data,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default)
        {
            _domain = data;
            return Task.CompletedTask;
        }
    }

    private static DomainIntelligence CachedCatchAllDomain() => new()
    {
        Domain = "example.com",
        DomainExists = true,
        Dns = new DnsLookupResult(
            DnsStatus.Success, true, [new MxRecord(10, "mx.example.com")], false, TimeSpan.Zero),
        Provider = new ProviderDetectionResult(
            MailProvider.GenericSmtp, 0.8, TopologyFingerprint: "10:mx.example.com"),
        CatchAll = new CatchAllDetectionResult(
            CatchAllStatus.LikelyCatchAll, 2, 2, 0, 0,
            "Independent routing evidence confirms otherwise nonexistent recipients are routed.", 0.96)
        {
            ReasonCode = CatchAllReasonCode.IndependentRoutingEvidence,
            RecipientBehavior = DomainRecipientBehavior.CatchAll,
            ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            StrategyVersion = "1.2.0"
        },
        ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        EvidenceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(50),
        StrategyVersion = "1.2.0",
        IntelligencePolicyVersion = "2.0.0"
    };

    private static EmailValidationOptions LiveSettings() => new()
    {
        Smtp = new SmtpOptions { Enabled = true, MaxMxAttempts = 3 },
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

    private static SmtpProbeResult RecipientAccepted(string host, DateTimeOffset? observedAt = null)
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 250, "2.1.5", SmtpResponseCategory.Accepted,
            SmtpResponseTextClassification.Success, 1, MailProvider.GenericSmtp,
            host, 1, observedAt ?? DateTimeOffset.UtcNow, "250 2.1.5 OK");
        var session = RecipientSession(host, evidence.Category, 250, "2.1.5",
            SmtpResponseTextClassification.Success);
        return new(SmtpMailboxStatus.Accepted, 250, evidence.SanitizedResponse, TimeSpan.Zero,
            Evidence: evidence, SessionEvidence: session);
    }

    private static SmtpProbeResult TemporaryFailure(string host)
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 451, "4.7.1", SmtpResponseCategory.TemporaryFailure,
            SmtpResponseTextClassification.TemporaryCondition, 1, MailProvider.GenericSmtp,
            host, 1, DateTimeOffset.UtcNow, "451 4.7.1 Try again later");
        var session = RecipientSession(host, evidence.Category, 451, "4.7.1",
            SmtpResponseTextClassification.TemporaryCondition);
        return new(SmtpMailboxStatus.TemporaryFailure, 451, evidence.SanitizedResponse, TimeSpan.Zero,
            Evidence: evidence, SessionEvidence: session);
    }

    private static SmtpProbeResult MailboxFull(string host)
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 552, "5.2.2", SmtpResponseCategory.MailboxFull,
            SmtpResponseTextClassification.MailboxFull, 1, MailProvider.GenericSmtp,
            host, 1, DateTimeOffset.UtcNow, "552 5.2.2 Mailbox full");
        var session = RecipientSession(host, evidence.Category, 552, "5.2.2",
            SmtpResponseTextClassification.MailboxFull);
        return new(SmtpMailboxStatus.MailboxFull, 552, evidence.SanitizedResponse, TimeSpan.Zero,
            Evidence: evidence, SessionEvidence: session);
    }

    private static SmtpProbeResult RawMailboxFull(string host)
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 552, "5.2.2", SmtpResponseCategory.MailboxFull,
            SmtpResponseTextClassification.MailboxFull, 1, MailProvider.GenericSmtp,
            host, 1, DateTimeOffset.UtcNow, "552 5.2.2 Mailbox full");
        return new(SmtpMailboxStatus.MailboxFull, 552, evidence.SanitizedResponse, TimeSpan.Zero,
            Evidence: evidence);
    }

    private static SmtpProbeResult LocalCooldown(string host) => new(
        SmtpMailboxStatus.NotAttempted, null, "Local cooldown", TimeSpan.Zero, Attempts: 0,
        Evidence: new SmtpEvidence(
            SmtpCommand.Connect, null, null, SmtpResponseCategory.LocalCooldown,
            SmtpResponseTextClassification.VerificationUnavailable, 0, MailProvider.GenericSmtp,
            host, 0, DateTimeOffset.UtcNow, "Local cooldown"))
    {
        Disposition = SmtpProbeDisposition.LocalCooldown,
        RetryAfter = DateTimeOffset.UtcNow.AddMinutes(1)
    };

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
