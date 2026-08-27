using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class SmtpReputationProtectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnforcedMailboxBudget_DefersImmediateRepeatButNotAnotherMailbox()
    {
        var clock = new ManualTimeProvider(Now);
        var service = Service(Settings(SmtpReputationProtectionMode.Enforced), clock);
        var first = await service.EvaluateAsync(Context("first@example.test"));
        var repeated = await service.EvaluateAsync(Context("first@example.test"));
        var other = await service.EvaluateAsync(Context("other@example.test"));

        Assert.Equal(SmtpProbeBudgetDecision.Allow, first.Decision);
        Assert.Equal(SmtpProbeBudgetDecision.Delay, repeated.Decision);
        Assert.Equal(SmtpReputationScopeType.Mailbox, repeated.RestrictingScope);
        Assert.Equal(Now.AddHours(1), repeated.RetryAtUtc);
        Assert.Equal(SmtpProbeBudgetDecision.Allow, other.Decision);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(SmtpProbeBudgetDecision.Allow,
            (await service.EvaluateAsync(Context("first@example.test"))).Decision);
    }

    [Fact]
    public async Task ConcurrentWorkers_AtomicallyAdmitOnlyOneMailboxProbe()
    {
        var settings = Settings(SmtpReputationProtectionMode.Enforced);
        var store = new InMemorySmtpReputationStateStore();
        var services = Enumerable.Range(0, 8)
            .Select(_ => Service(settings, new ManualTimeProvider(Now), store))
            .ToArray();

        var decisions = await Task.WhenAll(services.Select(service => service.EvaluateAsync(Context())));

        Assert.Equal(1, decisions.Count(decision => decision.Decision == SmtpProbeBudgetDecision.Allow));
        Assert.Equal(7, decisions.Count(decision => decision.Decision == SmtpProbeBudgetDecision.Delay));
    }

    [Fact]
    public async Task ObserveMode_ReportsWouldDelayWithoutSuppressingSmtp()
    {
        var service = Service(Settings(SmtpReputationProtectionMode.Observe), new ManualTimeProvider(Now));

        await service.EvaluateAsync(Context());
        var decision = await service.EvaluateAsync(Context());

        Assert.Equal(SmtpProbeBudgetDecision.Allow, decision.Decision);
        Assert.Equal(SmtpProbeBudgetDecision.Delay, decision.WouldDecision);
        Assert.False(decision.SuppressSmtp);
    }

    [Fact]
    public async Task DisabledMode_DoesNotReadOrWriteSharedState()
    {
        var settings = Settings(SmtpReputationProtectionMode.Enforced);
        settings.SmtpReputationProtection.Enabled = false;
        var service = Service(settings, new ManualTimeProvider(Now), new ThrowingStore());

        var decision = await service.EvaluateAsync(Context());
        await service.RecordAsync(Observation(Context(), SmtpNormalizedReason.PolicyBlock, true));

        Assert.Equal(SmtpProbeBudgetDecision.Allow, decision.Decision);
        Assert.Equal(SmtpReputationState.Disabled, decision.CircuitState);
        Assert.False(decision.SuppressSmtp);
    }

    [Fact]
    public async Task ExplicitUnknownRecipientPressure_OpensOnlyDomainCircuit()
    {
        var settings = Settings(SmtpReputationProtectionMode.Enforced);
        settings.SmtpReputationProtection.Mailbox.MinimumMinutesBetweenLiveProbes = 0;
        settings.SmtpReputationProtection.UnknownRecipientPressure.MinimumRcptObservations = 3;
        settings.SmtpReputationProtection.UnknownRecipientPressure.OpenRatio = 0.60;
        var store = new InMemorySmtpReputationStateStore();
        var service = Service(settings, new ManualTimeProvider(Now), store);
        var context = Context();

        await service.RecordAsync(Observation(context, SmtpNormalizedReason.MailboxNotFound, true));
        await service.RecordAsync(Observation(context, SmtpNormalizedReason.PolicyBlock, true));
        await service.RecordAsync(Observation(context, SmtpNormalizedReason.RecipientRejected, true));

        var states = await store.GetManyAsync([
            (SmtpReputationScopeType.RecipientDomain, "example.test"),
            (SmtpReputationScopeType.ProviderIdentity, "Microsoft365|smtp-162")]);
        var domain = Assert.Single(states, state => state.ScopeType == SmtpReputationScopeType.RecipientDomain);
        var identity = Assert.Single(states, state => state.ScopeType == SmtpReputationScopeType.ProviderIdentity);
        Assert.Equal(2, domain.UnknownRecipientCount);
        Assert.Equal(1, domain.PolicyBlockCount);
        Assert.Equal(SmtpReputationState.CircuitOpen, domain.State);
        Assert.Equal(2, identity.UnknownRecipientCount);
        Assert.NotEqual(SmtpReputationState.CircuitOpen, identity.State);
    }

    [Fact]
    public async Task ProviderPressure_RequiresMultipleIdentitiesAndDoesNotAffectGoogle()
    {
        var settings = LowThresholdSettings();
        var store = new InMemorySmtpReputationStateStore();
        var service = Service(settings, new ManualTimeProvider(Now), store);
        foreach (var identity in new[] { "smtp-162", "smtp-163", "smtp-162", "smtp-163" })
        {
            var context = Context(identity: identity);
            await service.RecordAsync(Observation(context, SmtpNormalizedReason.ProviderRateLimit, false));
        }

        var microsoft = await service.EvaluateAsync(Context(reserve: false));
        var google = await service.EvaluateAsync(Context(
            mailbox: "person@other.test", provider: MailProvider.GoogleWorkspace,
            identity: "smtp-167", reserve: false));

        Assert.Equal(SmtpProbeBudgetDecision.CircuitOpen, microsoft.Decision);
        Assert.Equal(SmtpReputationScopeType.Provider, microsoft.RestrictingScope);
        Assert.Equal(SmtpProbeBudgetDecision.Allow, google.Decision);
    }

    [Fact]
    public async Task NetworkCircuit_RequiresCrossProviderAndCrossIdentityBreadth()
    {
        var settings = LowThresholdSettings();
        var store = new InMemorySmtpReputationStateStore();
        var service = Service(settings, new ManualTimeProvider(Now), store);
        var contexts = new[]
        {
            Context(identity: "smtp-162"),
            Context(identity: "smtp-163"),
            Context(provider: MailProvider.GoogleWorkspace, identity: "smtp-167"),
            Context(provider: MailProvider.GoogleWorkspace, identity: "smtp-168")
        };
        foreach (var context in contexts)
            await service.RecordAsync(Observation(context, SmtpNormalizedReason.IpPolicyBlock, false));

        var decision = await service.EvaluateAsync(Context(reserve: false));

        Assert.Equal(SmtpProbeBudgetDecision.CircuitOpen, decision.Decision);
        Assert.Equal(SmtpReputationScopeType.NetworkBlock, decision.RestrictingScope);
    }

    [Fact]
    public async Task HalfOpenBudgetIsBoundedAndRecoveryIsGradual()
    {
        var settings = Settings(SmtpReputationProtectionMode.Enforced);
        settings.SmtpReputationProtection.Mailbox.MinimumMinutesBetweenLiveProbes = 0;
        settings.SmtpReputationProtection.Mailbox.MaximumLiveProbesPer24Hours = 20;
        settings.SmtpReputationProtection.CircuitBreaker.HalfOpenMaximumProbes = 2;
        settings.SmtpReputationProtection.CircuitBreaker.RecoverySuccessesRequired = 3;
        var clock = new ManualTimeProvider(Now);
        var store = new InMemorySmtpReputationStateStore();
        await store.TrySaveAsync(new SmtpReputationScopeSnapshot
        {
            ScopeType = SmtpReputationScopeType.NetworkBlock,
            ScopeId = "64.182.22.160/28",
            State = SmtpReputationState.CircuitOpen,
            WindowStartedAtUtc = Now.AddHours(-1),
            CooldownUntilUtc = Now,
            PolicyVersion = "test-v1",
            Version = 1
        }, 0);
        var service = Service(settings, clock, store);

        Assert.Equal(SmtpProbeBudgetDecision.Allow, (await service.EvaluateAsync(Context())).Decision);
        Assert.Equal(SmtpProbeBudgetDecision.Allow,
            (await service.EvaluateAsync(Context(mailbox: "second@example.test"))).Decision);
        var blocked = await service.EvaluateAsync(Context(mailbox: "third@example.test"));
        Assert.Equal(SmtpProbeBudgetDecision.DeferToDurableRetry, blocked.Decision);

        for (var index = 0; index < 2; index++)
            await service.RecordAsync(Observation(Context(), SmtpNormalizedReason.RecipientAccepted, true,
                SmtpResponseCategory.Accepted));
        var degraded = await State(store, SmtpReputationScopeType.NetworkBlock, "64.182.22.160/28");
        Assert.Equal(SmtpReputationState.Degraded, degraded.State);

        await service.RecordAsync(Observation(Context(), SmtpNormalizedReason.RecipientAccepted, true,
            SmtpResponseCategory.Accepted));
        var healthy = await State(store, SmtpReputationScopeType.NetworkBlock, "64.182.22.160/28");
        Assert.Equal(SmtpReputationState.Healthy, healthy.State);
    }

    [Theory]
    [InlineData(SmtpReputationProtectionMode.Observe, SmtpProbeBudgetDecision.Allow)]
    [InlineData(SmtpReputationProtectionMode.Enforced, SmtpProbeBudgetDecision.SafeFallback)]
    public async Task RepositoryFailureUsesModeAppropriateSafeFallback(
        SmtpReputationProtectionMode mode,
        SmtpProbeBudgetDecision expected)
    {
        var service = Service(Settings(mode), new ManualTimeProvider(Now), new ThrowingStore());

        var decision = await service.EvaluateAsync(Context());

        Assert.Equal(expected, decision.Decision);
        Assert.Equal(mode == SmtpReputationProtectionMode.Enforced, decision.SuppressSmtp);
    }

    [Theory]
    [InlineData(SmtpReputationProtectionMode.Observe, SmtpProbeBudgetDecision.Allow)]
    [InlineData(SmtpReputationProtectionMode.Enforced, SmtpProbeBudgetDecision.SafeFallback)]
    public async Task ReservationWriteFailureUsesModeAppropriateSafeFallback(
        SmtpReputationProtectionMode mode,
        SmtpProbeBudgetDecision expected)
    {
        var service = Service(Settings(mode), new ManualTimeProvider(Now), new WriteThrowingStore());

        var decision = await service.EvaluateAsync(Context());

        Assert.Equal(expected, decision.Decision);
        Assert.Equal(mode == SmtpReputationProtectionMode.Enforced, decision.SuppressSmtp);
    }

    [Fact]
    public async Task MailboxFullAndPolicyBlockAreAttributedSeparately()
    {
        var store = new InMemorySmtpReputationStateStore();
        var service = Service(Settings(SmtpReputationProtectionMode.Observe),
            new ManualTimeProvider(Now), store);
        await service.RecordAsync(Observation(Context(), SmtpNormalizedReason.MailboxFull, true,
            SmtpResponseCategory.MailboxFull));
        await service.RecordAsync(Observation(Context(), SmtpNormalizedReason.ReputationBlocked, false,
            SmtpResponseCategory.VerificationBlocked));

        var domain = await State(store, SmtpReputationScopeType.RecipientDomain, "example.test");
        Assert.Equal(0, domain.UnknownRecipientCount);
        Assert.Equal(1, domain.PolicyBlockCount);
        Assert.Equal(1, domain.RcptCount);
    }

    [Fact]
    public async Task ConcurrentObservationsUseVersionedAtomicUpdates()
    {
        var settings = LowThresholdSettings();
        settings.SmtpReputationProtection.CircuitBreaker.ProviderIdentityPolicyBlockCount = 100;
        var store = new InMemorySmtpReputationStateStore();
        var service = Service(settings, new ManualTimeProvider(Now), store);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.RecordAsync(
            Observation(Context(), SmtpNormalizedReason.ProviderRateLimit, false))));

        var provider = await State(store, SmtpReputationScopeType.Provider, "Microsoft365");
        Assert.Equal(20, provider.ConnectionCount);
        Assert.Equal(20, provider.PolicyBlockCount);
        Assert.Equal(20, provider.Version);
    }

    [Fact]
    public void MongoReputationDocumentsRoundTripAdditiveStateAndObservationFields()
    {
        var state = new SmtpReputationScopeSnapshot
        {
            ScopeType = SmtpReputationScopeType.ProviderIdentity,
            ScopeId = "Microsoft365|smtp-162",
            Provider = MailProvider.Microsoft365,
            State = SmtpReputationState.CircuitOpen,
            WindowStartedAtUtc = Now,
            ConnectionCount = 20,
            RcptCount = 10,
            UnknownRecipientCount = 2,
            PolicyBlockCount = 8,
            AffectedIdentityIds = ["smtp-162"],
            CooldownUntilUtc = Now.AddMinutes(30),
            PolicyVersion = "test-v1",
            Version = 7
        };
        var stateDocument = MongoSmtpReputationStateStore.SmtpReputationStateDocument.FromModel(state);
        var restoredState = stateDocument.ToModel();
        Assert.Equal(state.ScopeType, restoredState.ScopeType);
        Assert.Equal(state.ScopeId, restoredState.ScopeId);
        Assert.Equal(state.State, restoredState.State);
        Assert.Equal(state.ConnectionCount, restoredState.ConnectionCount);
        Assert.Equal(state.PolicyBlockCount, restoredState.PolicyBlockCount);
        Assert.Equal(state.AffectedIdentityIds, restoredState.AffectedIdentityIds);
        Assert.Equal(state.PolicyVersion, restoredState.PolicyVersion);
        Assert.Equal(state.Version, restoredState.Version);

        var evidence = new SmtpReputationEvidence
        {
            Decision = SmtpProbeBudgetDecision.CircuitOpen,
            WouldDecision = SmtpProbeBudgetDecision.CircuitOpen,
            Mode = SmtpReputationProtectionMode.Enforced,
            RestrictingScope = SmtpReputationScopeType.ProviderIdentity,
            CircuitState = SmtpReputationState.CircuitOpen,
            RetryAtUtc = Now.AddMinutes(30),
            SuppressionReason = "ProviderIdentityCircuitOpen",
            EvaluatedAtUtc = Now,
            PolicyVersion = "test-v1"
        };
        var observation = new ValidationObservation(
            "example.test", ValidationObservationType.MailboxProbe, MailProvider.Microsoft365,
            "mx.example.test", CatchAllStatus.NotCatchAll, 0.9,
            SmtpResponseCategory.LocalCooldown, Now, 0, Reputation: evidence);
        var observationDocument = MongoValidationIntelligenceStore.ValidationObservationDocument
            .FromModel(observation);
        var restored = observationDocument.ToModel();

        Assert.Equal(SmtpReputationProtectionMode.Enforced, restored.Reputation!.Mode);
        Assert.Equal(SmtpReputationScopeType.ProviderIdentity, restored.Reputation.RestrictingScope);
        Assert.Equal("test-v1", restored.Reputation.PolicyVersion);
    }

    [Fact]
    public void StaticOptionsRejectUnsafeReputationThresholdsAndNetworkMismatch()
    {
        var settings = Settings(SmtpReputationProtectionMode.Enforced);
        settings.OutboundIdentities.Enabled = true;
        settings.OutboundIdentities.AllowedCidr = "192.0.2.0/28";
        settings.SmtpReputationProtection.PolicyBlockPressure.DegradedRatio = 0.9;
        settings.SmtpReputationProtection.PolicyBlockPressure.OpenRatio = 0.2;

        var result = new EmailValidationOptionsValidator().Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("network block", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("pressure thresholds", StringComparison.OrdinalIgnoreCase));
    }

    private static SmtpReputationProtectionService Service(
        EmailValidationOptions settings,
        TimeProvider clock,
        ISmtpReputationStateStore? store = null)
    {
        var options = Options.Create(settings);
        return new(store ?? new InMemorySmtpReputationStateStore(), new SmtpReputationPolicy(options),
            options, clock, NullLogger<SmtpReputationProtectionService>.Instance);
    }

    private static EmailValidationOptions Settings(SmtpReputationProtectionMode mode) => new()
    {
        SmtpReputationProtection = new()
        {
            Enabled = true,
            Mode = mode,
            NetworkBlock = "64.182.22.160/28",
            PolicyVersion = "test-v1",
            WindowMinutes = 60,
            Mailbox = new()
            {
                MinimumMinutesBetweenLiveProbes = 60,
                MaximumLiveProbesPer24Hours = 2
            },
            CircuitBreaker = new()
            {
                Enabled = true,
                MinimumObservationsBeforeEvaluation = 20,
                CooldownMinutes = 30,
                HalfOpenMaximumProbes = 2,
                RecoverySuccessesRequired = 3,
                ProviderIdentityPolicyBlockCount = 3,
                ProviderAffectedIdentityCount = 2,
                NetworkAffectedProviderCount = 2,
                NetworkAffectedIdentityCount = 3
            },
            UnknownRecipientPressure = new()
            {
                Enabled = true,
                MinimumRcptObservations = 20,
                OpenRatio = 0.5
            },
            PolicyBlockPressure = new()
            {
                Enabled = true,
                MinimumObservations = 10,
                DegradedRatio = 0.15,
                OpenRatio = 0.3
            }
        }
    };

    private static EmailValidationOptions LowThresholdSettings()
    {
        var settings = Settings(SmtpReputationProtectionMode.Enforced);
        settings.SmtpReputationProtection.Mailbox.MinimumMinutesBetweenLiveProbes = 0;
        settings.SmtpReputationProtection.Mailbox.MaximumLiveProbesPer24Hours = 100;
        settings.SmtpReputationProtection.CircuitBreaker.MinimumObservationsBeforeEvaluation = 4;
        settings.SmtpReputationProtection.CircuitBreaker.ProviderIdentityPolicyBlockCount = 100;
        settings.SmtpReputationProtection.PolicyBlockPressure.MinimumObservations = 4;
        settings.SmtpReputationProtection.PolicyBlockPressure.OpenRatio = 0.5;
        settings.SmtpReputationProtection.PolicyBlockPressure.DegradedRatio = 0.25;
        return settings;
    }

    private static SmtpReputationBudgetContext Context(
        string mailbox = "person@example.test",
        MailProvider provider = MailProvider.Microsoft365,
        string identity = "smtp-162",
        bool reserve = true) => new(
            mailbox, mailbox[(mailbox.IndexOf('@') + 1)..], provider, identity,
            identity.Replace("smtp-", "64.182.22.", StringComparison.Ordinal), "mx.example.test", reserve);

    private static SmtpReputationObservation Observation(
        SmtpReputationBudgetContext context,
        SmtpNormalizedReason reason,
        bool rcpt,
        SmtpResponseCategory category = SmtpResponseCategory.RecipientRejected) =>
        new(context, category, reason, true, rcpt, Now);

    private static async Task<SmtpReputationScopeSnapshot> State(
        InMemorySmtpReputationStateStore store,
        SmtpReputationScopeType type,
        string id)
    {
        var states = await store.GetManyAsync([(type, id)]);
        return Assert.Single(states);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }

    private sealed class ThrowingStore : ISmtpReputationStateStore
    {
        public Task<IReadOnlyList<SmtpReputationScopeSnapshot>> GetManyAsync(
            IReadOnlyList<(SmtpReputationScopeType ScopeType, string ScopeId)> scopes,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repository unavailable");

        public Task<SmtpReputationStateWriteResult> TrySaveAsync(
            SmtpReputationScopeSnapshot state,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repository unavailable");
    }

    private sealed class WriteThrowingStore : ISmtpReputationStateStore
    {
        public Task<IReadOnlyList<SmtpReputationScopeSnapshot>> GetManyAsync(
            IReadOnlyList<(SmtpReputationScopeType ScopeType, string ScopeId)> scopes,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SmtpReputationScopeSnapshot>>([]);

        public Task<SmtpReputationStateWriteResult> TrySaveAsync(
            SmtpReputationScopeSnapshot state,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repository write unavailable");
    }
}
