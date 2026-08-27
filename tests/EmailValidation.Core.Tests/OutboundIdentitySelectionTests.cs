using System.Net;
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
            EhloHostName = "smtp-162.email.digitalwarehouse.io",
            Enabled = true,
            FcrDnsState = ForwardConfirmedReverseDnsState.Valid
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
        Assert.Equal(identity.EhloHostName, result.SessionEvidence.EhloHost);
        Assert.Contains($"EHLO {identity.EhloHostName}\r\n", connection.Commands, StringComparison.Ordinal);
        Assert.DoesNotContain("EHLO example.test", connection.Commands, StringComparison.Ordinal);
    }

    private static RendezvousOutboundIdentitySelector Selector(
        IOptions<EmailValidationOptions> options,
        FakeDiscovery discovery,
        FakeHealthStore health) => new(
            options,
            discovery,
            new ValidFcrDns(),
            health,
            TimeProvider.System,
            NullLogger<RendezvousOutboundIdentitySelector>.Instance);

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
        public Task<OutboundIdentitySelectionResult> SelectAsync(
            OutboundIdentitySelectionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OutboundIdentitySelectionResult(
                identity, OutboundIdentitySelectionReason.Selected, "Microsoft", "v1", []));
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

    private sealed class ScriptedConnectionFactory(string responses) : ISmtpConnectionFactory, IDisposable
    {
        private readonly ScriptedStream _stream = new(responses);
        public IPAddress? LocalAddress { get; private set; }
        public string Commands => _stream.Commands;

        public Task<ISmtpConnection> ConnectAsync(
            string host,
            int port,
            IPAddress localAddress,
            CancellationToken cancellationToken = default)
        {
            LocalAddress = localAddress;
            return Task.FromResult<ISmtpConnection>(new Connection(_stream, localAddress));
        }

        public void Dispose() => _stream.Dispose();

        private sealed class Connection(ScriptedStream stream, IPAddress localAddress) : ISmtpConnection
        {
            public Stream Stream => stream;
            public string LocalAddress => localAddress.ToString();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
