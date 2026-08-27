using System.Net;
using System.Net.Sockets;
using System.Text;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class OutboundIdentitySelectionTests
{
    [Theory]
    [InlineData("gmail.com", MailProvider.GoogleWorkspace, "ExplicitProviderOwnedDomain")]
    [InlineData("outlook.com", MailProvider.MicrosoftConsumer, "ExplicitProviderOwnedDomain")]
    public void ProviderOwnedDomain_PrecedesMxTopology(
        string domain,
        MailProvider expected,
        string evidence)
    {
        var result = new MailProviderDetector().DetectWithConfidence(
            domain,
            [new MxRecord(10, "mx.unrelated.example")]);

        Assert.Equal(expected, result.Provider);
        Assert.Equal(1, result.Confidence);
        Assert.Contains(evidence, result.Evidence!);
    }

    [Theory]
    [InlineData("tenant-com.mail.protection.outlook.com", MailProvider.Microsoft365)]
    [InlineData("aspmx.l.google.com", MailProvider.GoogleWorkspace)]
    [InlineData("mx.unknown.example", MailProvider.GenericSmtp)]
    public void CustomDomain_UsesMxTopology(string mxHost, MailProvider expected)
    {
        var result = new MailProviderDetector().DetectWithConfidence(
            "customer.example",
            [new MxRecord(0, mxHost)]);

        Assert.Equal(expected, result.Provider);
    }

    [Fact]
    public async Task SameDomain_IsStableAcrossSelectorInstances()
    {
        var options = Options.Create(Config());
        var discovery = new FakeDiscovery("64.182.22.162", "64.182.22.163", "64.182.22.167");
        var health = new FakeHealthStore();
        var first = Selector(options, discovery, health);
        var second = Selector(options, discovery, health);

        var one = await first.SelectAsync(new("company.example", MailProvider.Microsoft365));
        var two = await second.SelectAsync(new("company.example", MailProvider.Microsoft365));

        Assert.True(one.Selected);
        Assert.Equal(one.Identity!.IdentityId, two.Identity!.IdentityId);
        Assert.Equal("v1", one.AlgorithmVersion);
    }

    [Fact]
    public async Task RemovingIdentity_OnlyRemapsDomainsOwnedByThatIdentity()
    {
        var options = Options.Create(Config());
        var discovery = new FakeDiscovery("64.182.22.162", "64.182.22.163", "64.182.22.167");
        var selector = Selector(options, discovery, new FakeHealthStore());
        var before = new Dictionary<string, string>();
        for (var index = 0; index < 100; index++)
        {
            var domain = $"tenant-{index}.example";
            before[domain] = (await selector.SelectAsync(new(domain, MailProvider.Microsoft365)))
                .Identity!.IdentityId;
        }
        var removed = before.Values.First();
        discovery.Remove(removed == "smtp-162" ? "64.182.22.162" : "64.182.22.163");

        foreach (var item in before)
        {
            var after = (await selector.SelectAsync(new(item.Key, MailProvider.Microsoft365)))
                .Identity!.IdentityId;
            if (!string.Equals(item.Value, removed, StringComparison.Ordinal))
                Assert.Equal(item.Value, after);
        }
    }

    [Fact]
    public async Task UnavailableMicrosoftGroup_DoesNotFallBackToGoogle()
    {
        var options = Options.Create(Config());
        var selector = Selector(
            options,
            new FakeDiscovery("64.182.22.167"),
            new FakeHealthStore());

        var result = await selector.SelectAsync(new("company.example", MailProvider.Microsoft365));

        Assert.False(result.Selected);
        Assert.Equal("Microsoft", result.ProviderGroup);
        Assert.NotEqual(OutboundIdentitySelectionReason.Selected, result.Reason);
    }

    [Fact]
    public async Task ProviderCooldown_IsScopedToProvider()
    {
        var options = Options.Create(Config(sharedIdentity: true));
        var health = new FakeHealthStore();
        health.Set("smtp-162", MailProvider.Microsoft365, OutboundIdentityHealthState.Cooldown,
            DateTimeOffset.UtcNow.AddHours(1));
        var selector = Selector(options, new FakeDiscovery("64.182.22.162"), health);

        var microsoft = await selector.SelectAsync(new("company.example", MailProvider.Microsoft365));
        var google = await selector.SelectAsync(new("other.example", MailProvider.GoogleWorkspace));

        Assert.False(microsoft.Selected);
        Assert.True(google.Selected);
        Assert.Equal("smtp-162", google.Identity!.IdentityId);
    }

    [Theory]
    [InlineData(OutboundIdentityDnsReadinessMode.Observe, true)]
    [InlineData(OutboundIdentityDnsReadinessMode.Enforced, false)]
    public async Task Selector_HonorsDnsReadinessRolloutMode(
        OutboundIdentityDnsReadinessMode mode,
        bool expectedSelected)
    {
        var root = Config();
        root.OutboundIdentities.DnsReadiness.Mode = mode;
        var selector = Selector(Options.Create(root),
            new FakeDiscovery("64.182.22.162", "64.182.22.163"),
            new FakeHealthStore(),
            new FixedReadiness(ForwardConfirmedReverseDnsState.MissingPtr,
                isEligible: mode == OutboundIdentityDnsReadinessMode.Observe));

        var result = await selector.SelectAsync(new("company.example", MailProvider.Microsoft365));

        Assert.Equal(expectedSelected, result.Selected);
        if (!expectedSelected)
            Assert.Equal(OutboundIdentitySelectionReason.NoDnsReadyIdentities, result.Reason);
    }

    [Fact]
    public void HealthPolicy_DoesNotPenalizeRecipientFailureOrSingleTimeout()
    {
        var options = Options.Create(Config());
        var policy = new OutboundIdentityHealthPolicy(options, TimeProvider.System);
        var now = DateTimeOffset.UtcNow;

        var recipient = policy.Evaluate(new(
            "smtp-162", MailProvider.Microsoft365, SmtpResponseCategory.RecipientRejected,
            SmtpCooldownScope.None, SmtpHealthImpact.None, now), MailProvider.Unknown, 0);
        var timeout = policy.Evaluate(new(
            "smtp-162", MailProvider.Microsoft365, SmtpResponseCategory.Timeout,
            SmtpCooldownScope.None, SmtpHealthImpact.TemporaryFailure, now), MailProvider.Unknown, 0);

        Assert.Equal(OutboundIdentityHealthState.Healthy, recipient.State);
        Assert.Equal(OutboundIdentityHealthState.Healthy, timeout.State);
    }

    [Fact]
    public void HealthPolicy_ProviderRestrictionCreatesExpiringCooldown()
    {
        var options = Options.Create(Config());
        var policy = new OutboundIdentityHealthPolicy(options, TimeProvider.System);
        var retryAfter = DateTimeOffset.UtcNow.AddMinutes(30);

        var state = policy.Evaluate(new(
            "smtp-162", MailProvider.Microsoft365, SmtpResponseCategory.VerificationBlocked,
            SmtpCooldownScope.OutboundIdentity, SmtpHealthImpact.Restriction,
            DateTimeOffset.UtcNow, retryAfter), MailProvider.Microsoft365, 0);

        Assert.Equal(OutboundIdentityHealthState.Cooldown, state.State);
        Assert.Equal(MailProvider.Microsoft365, state.Provider);
        Assert.Equal(retryAfter, state.CooldownUntil);
    }

    [Theory]
    [InlineData("64.182.22.160")]
    [InlineData("64.182.22.161")]
    [InlineData("64.182.22.175")]
    [InlineData("192.0.2.10")]
    public void ReservedOrOutsideAddress_IsRejected(string address)
    {
        var options = ValidRoot();
        options.OutboundIdentities = Config().OutboundIdentities;
        options.OutboundIdentities.Identities[0].Address = address;

        var result = new EmailValidationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("reserved", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("outside", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SmtpProbe_BindsSelectedAddressAndUsesMatchingEhlo()
    {
        var root = Config();
        root.Smtp.Enabled = true;
        root.Smtp.RetryCount = 0;
        var options = Options.Create(root);
        var identity = new OutboundIdentity
        {
            IdentityId = "smtp-162",
            Address = IPAddress.Parse("64.182.22.162"),
            InterfaceName = "ens19",
            ExpectedPtrHostName = "smtp-162.email.digitalwarehouse.io",
            EhloHostName = "smtp-162.email.digitalwarehouse.io",
            Enabled = true,
            FcrDnsState = ForwardConfirmedReverseDnsState.Valid,
            DnsReadiness = new()
            {
                IdentityId = "smtp-162",
                Address = IPAddress.Parse("64.182.22.162"),
                ExpectedHostName = "smtp-162.email.digitalwarehouse.io",
                EhloHostName = "smtp-162.email.digitalwarehouse.io",
                State = ForwardConfirmedReverseDnsState.Valid,
                DnsState = ForwardConfirmedReverseDnsState.Valid,
                IsEligible = true,
                EvaluatedAtUtc = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
                ExpiresAtUtc = new DateTimeOffset(2026, 8, 27, 13, 0, 0, TimeSpan.Zero),
                ValidationPolicyVersion = "test-v1"
            }
        };
        using var connection = new ScriptedConnectionFactory(
            "220 mx.example ESMTP\r\n" +
            "250-mx.example\r\n250 SMTPUTF8\r\n" +
            "250 2.1.0 sender accepted\r\n" +
            "550 5.1.1 recipient rejected\r\n" +
            "250 reset\r\n221 bye\r\n");
        var probe = new SmtpMailboxProbe(
            options,
            NullLogger<SmtpMailboxProbe>.Instance,
            new AllowThrottle(),
            new SmtpResponseClassifier(),
            new SenderPool(),
            new ProbeSenderAffinityStore(TimeProvider.System, options),
            new SmtpSessionBudget(),
            new ProviderPolicyResolver(options),
            new FixedSelector(identity),
            new FakeHealthStore(),
            connection);

        var result = await probe.ProbeAsync(
            "mx.example", "person@company.example", MailProvider.Microsoft365);

        Assert.Equal(identity.Address, connection.LocalAddress);
        Assert.Equal(identity.IdentityId, result.SessionEvidence!.OutboundIdentityId);
        Assert.Equal(identity.Address.ToString(), result.SessionEvidence.SourceAddress);
        Assert.Equal(identity.Address.ToString(), result.SessionEvidence.ConfiguredSourceIp);
        Assert.Equal(identity.Address.ToString(), result.SessionEvidence.ActualBoundSourceIp);
        Assert.Equal(identity.ExpectedPtrHostName, result.SessionEvidence.ExpectedPtrHostName);
        Assert.Equal(ForwardConfirmedReverseDnsState.Valid, result.SessionEvidence.FcrDnsState);
        Assert.Equal("test-v1", result.SessionEvidence.FcrDnsPolicyVersion);
        Assert.Equal(identity.EhloHostName, result.SessionEvidence.EhloHost);
        Assert.Contains($"EHLO {identity.EhloHostName}\r\n", connection.Commands, StringComparison.Ordinal);
        Assert.DoesNotContain("EHLO example.test", connection.Commands, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmtpProbe_ActualEndpointMismatchStopsBeforeSmtpCommands()
    {
        var root = Config();
        root.Smtp.Enabled = true;
        root.Smtp.RetryCount = 0;
        var options = Options.Create(root);
        var identity = new OutboundIdentity
        {
            IdentityId = "smtp-162",
            Address = IPAddress.Parse("64.182.22.162"),
            InterfaceName = "ens19",
            ExpectedPtrHostName = "smtp-162.email.digitalwarehouse.io",
            EhloHostName = "smtp-162.email.digitalwarehouse.io",
            Enabled = true,
            FcrDnsState = ForwardConfirmedReverseDnsState.Valid
        };
        using var connection = new ScriptedConnectionFactory(
            "220 mx.example ESMTP\r\n", "64.182.232.51");
        var probe = new SmtpMailboxProbe(
            options, NullLogger<SmtpMailboxProbe>.Instance, new AllowThrottle(),
            new SmtpResponseClassifier(), new SenderPool(),
            new ProbeSenderAffinityStore(TimeProvider.System, options), new SmtpSessionBudget(),
            new ProviderPolicyResolver(options), new FixedSelector(identity), new FakeHealthStore(), connection);

        var result = await probe.ProbeAsync(
            "mx.example", "person@company.example", MailProvider.Microsoft365);

        Assert.True(result.LocalBindFailure);
        Assert.Equal(SmtpMailboxStatus.ConnectionFailure, result.Status);
        Assert.Equal("64.182.232.51", result.SessionEvidence!.ActualBoundSourceIp);
        Assert.Empty(connection.Commands);
    }

    [Fact]
    public async Task SmtpProbe_BindFailureDoesNotFallBackToUnboundConnection()
    {
        var root = Config();
        root.Smtp.Enabled = true;
        root.Smtp.RetryCount = 0;
        var options = Options.Create(root);
        var identity = new OutboundIdentity
        {
            IdentityId = "smtp-162",
            Address = IPAddress.Parse("64.182.22.162"),
            InterfaceName = "ens19",
            ExpectedPtrHostName = "smtp-162.email.digitalwarehouse.io",
            EhloHostName = "smtp-162.email.digitalwarehouse.io",
            Enabled = true,
            FcrDnsState = ForwardConfirmedReverseDnsState.Valid
        };
        var connection = new ThrowingBindConnectionFactory();
        var probe = new SmtpMailboxProbe(
            options, NullLogger<SmtpMailboxProbe>.Instance, new AllowThrottle(),
            new SmtpResponseClassifier(), new SenderPool(),
            new ProbeSenderAffinityStore(TimeProvider.System, options), new SmtpSessionBudget(),
            new ProviderPolicyResolver(options), new FixedSelector(identity), new FakeHealthStore(), connection);

        var result = await probe.ProbeAsync(
            "mx.example", "person@company.example", MailProvider.Microsoft365);

        Assert.True(result.LocalBindFailure);
        Assert.Equal(SmtpMailboxStatus.ConnectionFailure, result.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, connection.Calls);
        Assert.Equal(identity.Address, connection.LocalAddress);
    }

    [Fact]
    public async Task SmtpProbe_NoEligibleIdentityDoesNotOpenConnection()
    {
        var root = Config();
        root.Smtp.Enabled = true;
        var options = Options.Create(root);
        var connection = new CountingConnectionFactory();
        var probe = new SmtpMailboxProbe(
            options, NullLogger<SmtpMailboxProbe>.Instance, new AllowThrottle(),
            new SmtpResponseClassifier(), new SenderPool(),
            new ProbeSenderAffinityStore(TimeProvider.System, options), new SmtpSessionBudget(),
            new ProviderPolicyResolver(options), new UnavailableSelector(), new FakeHealthStore(), connection);

        var result = await probe.ProbeAsync(
            "mx.example", "person@company.example", MailProvider.Microsoft365);

        Assert.Equal(SmtpMailboxStatus.NotAttempted, result.Status);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(SmtpNormalizedReason.OutboundIdentityDnsNotReady,
            result.Evidence!.Intelligence!.Reason);
        Assert.NotNull(result.RetryAfter);
        Assert.Equal(0, connection.Calls);
    }

    [Fact]
    public async Task SmtpProbe_EnforcedReputationDecisionPreventsConnection()
    {
        var root = Config();
        root.Smtp.Enabled = true;
        var options = Options.Create(root);
        var identity = IdentityModel();
        var connection = new CountingConnectionFactory();
        var protection = new FixedReputationProtection(new SmtpReputationEvidence
        {
            Decision = SmtpProbeBudgetDecision.CircuitOpen,
            WouldDecision = SmtpProbeBudgetDecision.CircuitOpen,
            Mode = SmtpReputationProtectionMode.Enforced,
            RestrictingScope = SmtpReputationScopeType.NetworkBlock,
            CircuitState = SmtpReputationState.CircuitOpen,
            RetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            SuppressionReason = "NetworkBlockCircuitOpen",
            EvaluatedAtUtc = DateTimeOffset.UtcNow,
            PolicyVersion = "test-v1"
        });
        var probe = new SmtpMailboxProbe(
            options, NullLogger<SmtpMailboxProbe>.Instance, new AllowThrottle(),
            new SmtpResponseClassifier(), new SenderPool(),
            new ProbeSenderAffinityStore(TimeProvider.System, options), new SmtpSessionBudget(),
            new ProviderPolicyResolver(options), new FixedSelector(identity), new FakeHealthStore(),
            connection, protection);

        var result = await probe.ProbeAsync(
            "mx.example", "person@company.example", MailProvider.Microsoft365);

        Assert.Equal(SmtpMailboxStatus.NotAttempted, result.Status);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(0, connection.Calls);
        Assert.Equal(SmtpNormalizedReason.ReputationPolicyDeferred,
            result.Evidence!.Intelligence!.Reason);
        Assert.Equal(SmtpReputationScopeType.NetworkBlock,
            result.Evidence.Reputation!.RestrictingScope);
        Assert.Equal(0, protection.RecordCalls);
    }

    [Fact]
    public async Task SmtpProbe_ReputationEvaluationFailureFailsSafeWhenEnforced()
    {
        var root = Config();
        root.Smtp.Enabled = true;
        root.SmtpReputationProtection.Mode = SmtpReputationProtectionMode.Enforced;
        var options = Options.Create(root);
        var connection = new CountingConnectionFactory();
        var probe = new SmtpMailboxProbe(
            options, NullLogger<SmtpMailboxProbe>.Instance, new AllowThrottle(),
            new SmtpResponseClassifier(), new SenderPool(),
            new ProbeSenderAffinityStore(TimeProvider.System, options), new SmtpSessionBudget(),
            new ProviderPolicyResolver(options), new FixedSelector(IdentityModel()), new FakeHealthStore(),
            connection, new ThrowingReputationProtection());

        var result = await probe.ProbeAsync(
            "mx.example", "person@company.example", MailProvider.Microsoft365);

        Assert.Equal(SmtpMailboxStatus.NotAttempted, result.Status);
        Assert.Equal(0, connection.Calls);
        Assert.Equal(SmtpProbeBudgetDecision.SafeFallback, result.Evidence!.Reputation!.Decision);
    }

    [Fact]
    public async Task SmtpProbe_ObserveDecisionRecordsComparisonWithoutSuppressing()
    {
        var root = Config();
        root.Smtp.Enabled = true;
        root.Smtp.RetryCount = 0;
        var options = Options.Create(root);
        var identity = IdentityModel();
        using var connection = new ScriptedConnectionFactory(
            "220 mx.example ESMTP\r\n250 mx.example\r\n250 sender accepted\r\n" +
            "550 5.1.1 recipient rejected\r\n250 reset\r\n221 bye\r\n");
        var protection = new FixedReputationProtection(new SmtpReputationEvidence
        {
            Decision = SmtpProbeBudgetDecision.Allow,
            WouldDecision = SmtpProbeBudgetDecision.CircuitOpen,
            Mode = SmtpReputationProtectionMode.Observe,
            RestrictingScope = SmtpReputationScopeType.Provider,
            CircuitState = SmtpReputationState.CircuitOpen,
            RetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            EvaluatedAtUtc = DateTimeOffset.UtcNow,
            PolicyVersion = "test-v1"
        });
        var selector = new FixedSelector(identity);
        var probe = new SmtpMailboxProbe(
            options, NullLogger<SmtpMailboxProbe>.Instance, new AllowThrottle(),
            new SmtpResponseClassifier(), new SenderPool(),
            new ProbeSenderAffinityStore(TimeProvider.System, options), new SmtpSessionBudget(),
            new ProviderPolicyResolver(options), selector, new FakeHealthStore(),
            connection, protection);

        var result = await probe.ProbeAsync(
            "mx.example", "person@company.example", MailProvider.Microsoft365);

        Assert.Equal(1, protection.RecordCalls);
        Assert.Equal(1, selector.Calls);
        Assert.Equal(SmtpProbeBudgetDecision.CircuitOpen, result.Evidence!.Reputation!.WouldDecision);
        Assert.Contains("RCPT TO", connection.Commands, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmtpProbe_RetriesDoNotCycleOutboundIdentity()
    {
        var root = Config();
        root.Smtp.Enabled = true;
        root.Smtp.RetryCount = 3;
        var options = Options.Create(root);
        var identity = IdentityModel();
        var selector = new FixedSelector(identity);
        using var connection = new ScriptedConnectionFactory("421 4.7.0 policy blocked\r\n");
        var probe = new SmtpMailboxProbe(
            options, NullLogger<SmtpMailboxProbe>.Instance, new AllowThrottle(),
            new SmtpResponseClassifier(), new SenderPool(),
            new ProbeSenderAffinityStore(TimeProvider.System, options), new SmtpSessionBudget(),
            new ProviderPolicyResolver(options), selector, new FakeHealthStore(), connection);

        var result = await probe.ProbeAsync(
            "mx.example", "person@company.example", MailProvider.Microsoft365);

        Assert.Equal(1, selector.Calls);
        Assert.NotEmpty(connection.LocalAddresses);
        Assert.All(connection.LocalAddresses, address => Assert.Equal(identity.Address, address));
        Assert.Equal(identity.Address, connection.LocalAddress);
        Assert.True(result.Attempts > 1);
    }

    private static RendezvousOutboundIdentitySelector Selector(
        IOptions<EmailValidationOptions> options,
        FakeDiscovery discovery,
        FakeHealthStore health,
        IForwardConfirmedReverseDnsValidator? readiness = null) => new(
            options,
            discovery,
            readiness ?? new ValidFcrDns(),
            health,
            TimeProvider.System,
            NullLogger<RendezvousOutboundIdentitySelector>.Instance);

    private static OutboundIdentity IdentityModel() => new()
    {
        IdentityId = "smtp-162",
        Address = IPAddress.Parse("64.182.22.162"),
        InterfaceName = "ens19",
        ExpectedPtrHostName = "smtp-162.email.digitalwarehouse.io",
        EhloHostName = "smtp-162.email.digitalwarehouse.io",
        Enabled = true,
        FcrDnsState = ForwardConfirmedReverseDnsState.Valid
    };

    private static EmailValidationOptions Config(bool sharedIdentity = false)
    {
        var options = ValidRoot();
        options.OutboundIdentities = new OutboundIdentityOptions
        {
            Enabled = true,
            InterfaceName = "ens19",
            AllowedCidr = "64.182.22.160/28",
            GatewayAddress = "64.182.22.161",
            SelectionAlgorithmVersion = "v1",
            ProviderGroups = new(StringComparer.OrdinalIgnoreCase)
            {
                [MailProvider.Microsoft365.ToString()] = "Microsoft",
                [MailProvider.GoogleWorkspace.ToString()] = "Google"
            },
            IdentityGroups = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft"] = ["smtp-162", "smtp-163"],
                ["Google"] = sharedIdentity ? ["smtp-162"] : ["smtp-167"]
            },
            Identities =
            [
                Identity(162),
                Identity(163),
                Identity(167)
            ]
        };
        return options;
    }

    private static EmailValidationOptions ValidRoot() => new()
    {
        ProbeSenderSource = new ProbeSenderSourceOptions
        {
            Index = "senders",
            QueryJson = "{}"
        }
    };

    private static OutboundIdentityConfiguration Identity(int octet) => new()
    {
        IdentityId = $"smtp-{octet}",
        Address = $"64.182.22.{octet}",
        InterfaceName = "ens19",
        ExpectedPtrHostName = $"smtp-{octet}.email.digitalwarehouse.io",
        EhloHostName = $"smtp-{octet}.email.digitalwarehouse.io"
    };

    private sealed class FakeDiscovery(params string[] addresses) : ILocalOutboundIdentityDiscovery
    {
        private readonly HashSet<string> _addresses = new(addresses, StringComparer.Ordinal);

        public void Remove(string address) => _addresses.Remove(address);

        public Task<IReadOnlySet<string>> GetBoundIpv4AddressesAsync(
            string interfaceName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(_addresses);
    }

    private sealed class ValidFcrDns : IForwardConfirmedReverseDnsValidator
    {
        public Task<ForwardConfirmedReverseDnsState> ValidateAsync(
            OutboundIdentity identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ForwardConfirmedReverseDnsState.Valid);
    }

    private sealed class FixedReadiness(
        ForwardConfirmedReverseDnsState state,
        bool isEligible) : IForwardConfirmedReverseDnsValidator
    {
        public Task<ForwardConfirmedReverseDnsState> ValidateAsync(
            OutboundIdentity identity,
            CancellationToken cancellationToken = default) => Task.FromResult(state);

        public Task<OutboundIdentityDnsReadiness> GetReadinessAsync(
            OutboundIdentity identity,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) => Task.FromResult(new OutboundIdentityDnsReadiness
            {
                IdentityId = identity.IdentityId,
                Address = identity.Address,
                ExpectedHostName = identity.ExpectedPtrHostName,
                EhloHostName = identity.EhloHostName,
                State = state,
                DnsState = state,
                IsEligible = isEligible,
                EvaluatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                ValidationPolicyVersion = "test-v1"
            });
    }

    private sealed class FakeHealthStore : IOutboundIdentityHealthStore
    {
        private readonly Dictionary<(string, MailProvider), OutboundIdentityHealth> _states = [];

        public void Set(
            string identityId,
            MailProvider provider,
            OutboundIdentityHealthState state,
            DateTimeOffset? until = null) =>
            _states[(identityId, provider)] = new(identityId, provider, state, until);

        public Task<OutboundIdentityHealth> GetAsync(
            string identityId,
            MailProvider provider,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_states.TryGetValue((identityId, provider), out var state)
                ? state
                : new OutboundIdentityHealth(identityId, provider, OutboundIdentityHealthState.Healthy));

        public Task RecordAsync(
            OutboundIdentityOutcome outcome,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedSelector(OutboundIdentity identity) : IOutboundIdentitySelector
    {
        public int Calls { get; private set; }

        public Task<OutboundIdentitySelectionResult> SelectAsync(
            OutboundIdentitySelectionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new OutboundIdentitySelectionResult(
                identity, OutboundIdentitySelectionReason.Selected, "Microsoft", "v1", []));
        }
    }

    private sealed class UnavailableSelector : IOutboundIdentitySelector
    {
        public Task<OutboundIdentitySelectionResult> SelectAsync(
            OutboundIdentitySelectionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OutboundIdentitySelectionResult(
                null, OutboundIdentitySelectionReason.NoDnsReadyIdentities, "Microsoft", "v1", []));
    }

    private sealed class SenderPool : IProbeSenderPool
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ProbeSenderSelection?> GetSenderAsync(
            ProbeSenderContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProbeSenderSelection?>(new(
                "probe@example.test", ProbeSenderCandidateState.Healthy));

        public Task RecordOutcomeAsync(
            ProbeSenderOutcome outcome,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ProbeSenderPoolSnapshot GetSnapshot() => new(
            "test", "test", 1, 1, 1, 0, null, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class AllowThrottle : ISmtpProbeThrottle
    {
        public ValueTask<ISmtpThrottleLease> AcquireAsync(
            SmtpThrottleContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ISmtpThrottleLease>(new Lease());

        private sealed class Lease : ISmtpThrottleLease
        {
            public bool Acquired => true;
            public DateTimeOffset? RetryAfter => null;
            public string? Reason => null;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedConnectionFactory(
        string responses,
        string? actualLocalAddress = null) : ISmtpConnectionFactory, IDisposable
    {
        private readonly ScriptedStream _stream = new(responses);
        public int Calls { get; private set; }
        public List<IPAddress> LocalAddresses { get; } = [];
        public IPAddress? LocalAddress { get; private set; }
        public string Commands => _stream.Commands;

        public Task<ISmtpConnection> ConnectAsync(
            string host,
            int port,
            IPAddress localAddress,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LocalAddresses.Add(localAddress);
            LocalAddress = localAddress;
            return Task.FromResult<ISmtpConnection>(new Connection(
                _stream, actualLocalAddress ?? localAddress.ToString()));
        }

        public void Dispose() => _stream.Dispose();

        private sealed class Connection(ScriptedStream stream, string localAddress) : ISmtpConnection
        {
            public Stream Stream => stream;
            public string LocalAddress => localAddress;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingBindConnectionFactory : ISmtpConnectionFactory
    {
        public int Calls { get; private set; }
        public IPAddress? LocalAddress { get; private set; }

        public Task<ISmtpConnection> ConnectAsync(
            string host,
            int port,
            IPAddress localAddress,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LocalAddress = localAddress;
            throw new OutboundIdentityBindException(
                localAddress, new SocketException((int)SocketError.AccessDenied));
        }
    }

    private sealed class CountingConnectionFactory : ISmtpConnectionFactory
    {
        public int Calls { get; private set; }

        public Task<ISmtpConnection> ConnectAsync(
            string host,
            int port,
            IPAddress localAddress,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("SMTP connection must not be attempted");
        }
    }

    private sealed class FixedReputationProtection(SmtpReputationEvidence decision)
        : ISmtpReputationProtection
    {
        public int RecordCalls { get; private set; }

        public Task<SmtpReputationEvidence> EvaluateAsync(
            SmtpReputationBudgetContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(decision);

        public Task RecordAsync(
            SmtpReputationObservation observation,
            CancellationToken cancellationToken = default)
        {
            RecordCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingReputationProtection : ISmtpReputationProtection
    {
        public Task<SmtpReputationEvidence> EvaluateAsync(
            SmtpReputationBudgetContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("policy unavailable");

        public Task RecordAsync(
            SmtpReputationObservation observation,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ScriptedStream(string responses) : Stream
    {
        private readonly MemoryStream _reads = new(Encoding.UTF8.GetBytes(responses));
        private readonly MemoryStream _writes = new();

        public string Commands => Encoding.UTF8.GetString(_writes.ToArray());
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _reads.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => _reads.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _writes.Write(buffer, offset, count);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => _writes.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
