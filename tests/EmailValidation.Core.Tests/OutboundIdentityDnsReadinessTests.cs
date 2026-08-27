using System.Net;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class OutboundIdentityDnsReadinessTests
{
    [Theory]
    [InlineData("smtp-162.email.digitalwarehouse.io", "smtp-162.email.digitalwarehouse.io")]
    [InlineData("SMTP-162.EMAIL.DIGITALWAREHOUSE.IO", "smtp-162.email.digitalwarehouse.io")]
    [InlineData("smtp-162.email.digitalwarehouse.io.", "smtp-162.email.digitalwarehouse.io")]
    [InlineData("münchen.example", "xn--mnchen-3ya.example")]
    public void HostNameNormalization_AcceptsCanonicalDnsNames(string value, string expected)
    {
        Assert.True(OutboundIdentityHostName.TryNormalize(value, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" smtp.example")]
    [InlineData("smtp .example")]
    [InlineData("smtp_name.example")]
    [InlineData("localhost")]
    [InlineData("-smtp.example")]
    [InlineData("smtp-.example")]
    [InlineData("64.182.22.162")]
    public void HostNameNormalization_RejectsInvalidNames(string value) =>
        Assert.False(OutboundIdentityHostName.TryNormalize(value, out _));

    [Fact]
    public void HostNameNormalization_RejectsLongLabelAndLongName()
    {
        Assert.False(OutboundIdentityHostName.TryNormalize($"{new string('a', 64)}.example", out _));
        Assert.False(OutboundIdentityHostName.TryNormalize(
            string.Join('.', Enumerable.Repeat(new string('a', 63), 5)), out _));
    }

    [Fact]
    public void StaticOptions_RequireExplicitValidExpectedPtrAndMatchingEhlo()
    {
        var settings = Settings();
        settings.OutboundIdentities.Identities[0].ExpectedPtrHostName = string.Empty;
        settings.OutboundIdentities.Identities[0].EhloHostName = "smtp_name.example";

        var result = new EmailValidationOptionsValidator().Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("expected PTR", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("EHLO", StringComparison.Ordinal));
    }

    [Fact]
    public void IdentityFactory_NormalizesExpectedPtrAndEhlo()
    {
        var settings = Settings();
        var configured = settings.OutboundIdentities.Identities[0];
        configured.ExpectedPtrHostName = "SMTP-162.EMAIL.DIGITALWAREHOUSE.IO.";
        configured.EhloHostName = "smtp-162.EMAIL.digitalwarehouse.IO";

        Assert.True(OutboundIdentityFactory.TryCreate(
            settings.OutboundIdentities, configured, out var identity));
        Assert.Equal("smtp-162.email.digitalwarehouse.io", identity.ExpectedPtrHostName);
        Assert.Equal(identity.ExpectedPtrHostName, identity.EhloHostName);
    }

    [Fact]
    public void StrictPolicy_RequiresOnePtrOneAddressAndMatchingEhlo()
    {
        var now = Instant();
        var policy = Policy(OutboundIdentityDnsReadinessMode.Enforced,
            ForwardConfirmedReverseDnsValidationMode.StrictOneToOne);
        var result = policy.Evaluate(Identity(), Bound(),
            Reverse("SMTP-162.EMAIL.DIGITALWAREHOUSE.IO."),
            Forward("64.182.22.162"), now);

        Assert.Equal(ForwardConfirmedReverseDnsState.Valid, result.State);
        Assert.True(result.IsEligible);
        Assert.Equal(now.AddMinutes(10), result.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(OutboundIdentityDnsQueryStatus.NoData, ForwardConfirmedReverseDnsState.MissingPtr)]
    [InlineData(OutboundIdentityDnsQueryStatus.NotFound, ForwardConfirmedReverseDnsState.MissingPtr)]
    [InlineData(OutboundIdentityDnsQueryStatus.ResolverUnavailable, ForwardConfirmedReverseDnsState.DnsResolverUnavailable)]
    [InlineData(OutboundIdentityDnsQueryStatus.TemporaryFailure, ForwardConfirmedReverseDnsState.DnsTemporaryFailure)]
    public void PtrFailures_AreClassified(
        OutboundIdentityDnsQueryStatus status,
        ForwardConfirmedReverseDnsState expected)
    {
        var result = Policy().Evaluate(Identity(), Bound(),
            new(status, [], []), Forward("64.182.22.162"), Instant());

        Assert.Equal(expected, result.DnsState);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void StrictAndCompatibleModes_HandleMultipleAnswersDifferently()
    {
        var reverse = Reverse("alias.example", "smtp-162.email.digitalwarehouse.io.");
        var forward = Forward("192.0.2.1", "64.182.22.162");

        var strict = Policy(validationMode: ForwardConfirmedReverseDnsValidationMode.StrictOneToOne)
            .Evaluate(Identity(), Bound(), reverse, forward, Instant());
        var compatible = Policy(validationMode: ForwardConfirmedReverseDnsValidationMode.CompatibleContainsMatch)
            .Evaluate(Identity(), Bound(), reverse, forward, Instant());

        Assert.Equal(ForwardConfirmedReverseDnsState.MultiplePtrRecords, strict.DnsState);
        Assert.False(strict.IsEligible);
        Assert.Equal(ForwardConfirmedReverseDnsState.Valid, compatible.DnsState);
        Assert.True(compatible.IsEligible);
        Assert.Contains(ForwardConfirmedReverseDnsState.MultiplePtrRecords, compatible.Warnings);
        Assert.Contains(ForwardConfirmedReverseDnsState.MultipleForwardAddresses, compatible.Warnings);
    }

    [Theory]
    [InlineData("wrong.example", "64.182.22.162", ForwardConfirmedReverseDnsState.UnexpectedPtr)]
    [InlineData("smtp-162.email.digitalwarehouse.io", "192.0.2.1", ForwardConfirmedReverseDnsState.ForwardAddressMismatch)]
    public void ConfirmedDnsMismatch_IsIneligible(
        string ptrHost,
        string address,
        ForwardConfirmedReverseDnsState expected)
    {
        var result = Policy().Evaluate(
            Identity(), Bound(), Reverse(ptrHost), Forward(address), Instant(), Instant().AddMinutes(-1));

        Assert.Equal(expected, result.DnsState);
        Assert.False(result.IsEligible);
        Assert.False(result.IsDegraded);
    }

    [Theory]
    [InlineData(OutboundIdentityDnsQueryStatus.NoData)]
    [InlineData(OutboundIdentityDnsQueryStatus.NotFound)]
    public void MissingForwardRecord_IsClassified(OutboundIdentityDnsQueryStatus status)
    {
        var result = Policy().Evaluate(Identity(), Bound(),
            Reverse("smtp-162.email.digitalwarehouse.io"),
            new(status, [], []), Instant());

        Assert.Equal(ForwardConfirmedReverseDnsState.MissingForwardRecord, result.DnsState);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void Ipv6OnlyForwardAnswer_DoesNotConfirmIpv4Identity()
    {
        var result = Policy().Evaluate(Identity(), Bound(),
            Reverse("smtp-162.email.digitalwarehouse.io"),
            new(OutboundIdentityDnsQueryStatus.Success, [], [IPAddress.Parse("2001:db8::162")]),
            Instant());

        Assert.Equal(ForwardConfirmedReverseDnsState.MissingForwardRecord, result.DnsState);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void EhloMismatch_IsAlwaysIneligibleWhenRequired()
    {
        var result = Policy(OutboundIdentityDnsReadinessMode.Observe).Evaluate(
            Identity() with { EhloHostName = "other.example" }, Bound(),
            Reverse("smtp-162.email.digitalwarehouse.io"), Forward("64.182.22.162"), Instant());

        Assert.Equal(ForwardConfirmedReverseDnsState.EhloMismatch, result.State);
        Assert.False(result.IsEligible);
    }

    [Theory]
    [InlineData(false, true, false, null, ForwardConfirmedReverseDnsState.WrongInterface)]
    [InlineData(true, false, false, null, ForwardConfirmedReverseDnsState.WrongInterface)]
    [InlineData(true, true, false, null, ForwardConfirmedReverseDnsState.LocalAddressNotBound)]
    [InlineData(true, true, false, "eth0", ForwardConfirmedReverseDnsState.WrongInterface)]
    public void LocalBindingFailures_AreHardExclusions(
        bool exists,
        bool operational,
        bool bound,
        string? actualInterface,
        ForwardConfirmedReverseDnsState expected)
    {
        var local = new LocalOutboundIdentityBinding("ens19", exists, operational, bound, actualInterface);
        var result = Policy(OutboundIdentityDnsReadinessMode.Observe).Evaluate(
            Identity(), local, Reverse("smtp-162.email.digitalwarehouse.io"),
            Forward("64.182.22.162"), Instant());

        Assert.Equal(expected, result.State);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void TemporaryFailure_UsesOnlyBoundedLastKnownGoodGrace()
    {
        var now = Instant();
        var policy = Policy();
        var recent = policy.Evaluate(Identity(), Bound(),
            new(OutboundIdentityDnsQueryStatus.TemporaryFailure, [], []),
            Forward("64.182.22.162"), now, now.AddMinutes(-5));
        var expired = policy.Evaluate(Identity(), Bound(),
            new(OutboundIdentityDnsQueryStatus.TemporaryFailure, [], []),
            Forward("64.182.22.162"), now, now.AddMinutes(-16));

        Assert.True(recent.IsEligible);
        Assert.True(recent.IsDegraded);
        Assert.False(expired.IsEligible);
    }

    [Fact]
    public void TemporaryFailure_WithoutLastKnownGood_IsNotEligible()
    {
        var result = Policy().Evaluate(Identity(), Bound(),
            new(OutboundIdentityDnsQueryStatus.TemporaryFailure, [], []),
            new(OutboundIdentityDnsQueryStatus.TemporaryFailure, [], []), Instant());

        Assert.Equal(ForwardConfirmedReverseDnsState.DnsTemporaryFailure, result.DnsState);
        Assert.False(result.IsEligible);
        Assert.False(result.IsDegraded);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(10, 10)]
    [InlineData(2880, 1440)]
    public void PositiveTtl_IsMinimumAndClamped(int ttlMinutes, int expectedMinutes)
    {
        var now = Instant();
        var reverse = Reverse("smtp-162.email.digitalwarehouse.io") with
        {
            TimeToLive = TimeSpan.FromMinutes(ttlMinutes)
        };
        var result = Policy().Evaluate(Identity(), Bound(), reverse,
            Forward("64.182.22.162") with { TimeToLive = TimeSpan.FromMinutes(3000) }, now);

        Assert.Equal(now.AddMinutes(expectedMinutes), result.ExpiresAtUtc);
    }

    [Fact]
    public void MissingPositiveTtl_UsesConfiguredFallback()
    {
        var now = Instant();
        var result = Policy().Evaluate(Identity(), Bound(),
            new(OutboundIdentityDnsQueryStatus.Success,
                ["smtp-162.email.digitalwarehouse.io"], []),
            new(OutboundIdentityDnsQueryStatus.Success, [], [IPAddress.Parse("64.182.22.162")]),
            now);

        Assert.Equal(now.AddMinutes(60), result.ExpiresAtUtc);
    }

    [Fact]
    public void ConfirmedMismatch_UsesBoundedNegativeCache()
    {
        var now = Instant();
        var result = Policy().Evaluate(Identity(), Bound(), Reverse("wrong.example"),
            Forward("64.182.22.162"), now);

        Assert.Equal(now.AddMinutes(5), result.ExpiresAtUtc);
    }

    [Fact]
    public async Task DisabledMode_SkipsDnsAndRetainsLocalBindingGate()
    {
        var settings = Settings();
        settings.OutboundIdentities.DnsReadiness.Mode = OutboundIdentityDnsReadinessMode.Disabled;
        var options = Options.Create(settings);
        var dns = new CountingDnsResolver();
        var validator = new ForwardConfirmedReverseDnsValidator(
            options, dns, new BoundDiscovery(), new OutboundIdentityReadinessPolicy(options),
            new ManualTimeProvider(Instant()), NullLogger<ForwardConfirmedReverseDnsValidator>.Instance);

        var readiness = await validator.GetReadinessAsync(Identity());

        Assert.True(readiness.IsEligible);
        Assert.Equal(ForwardConfirmedReverseDnsState.NotEvaluated, readiness.DnsState);
        Assert.Equal(0, dns.PtrCalls);
        Assert.Equal(0, dns.ForwardCalls);
    }

    [Fact]
    public async Task Validator_CachesAndSingleFlightsDnsRefresh()
    {
        var clock = new ManualTimeProvider(Instant());
        var dns = new BlockingDnsResolver();
        var validator = Validator(clock, dns);
        var calls = Enumerable.Range(0, 8)
            .Select(_ => validator.GetReadinessAsync(Identity(), true)).ToArray();
        await dns.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dns.Release.TrySetResult();
        var results = await Task.WhenAll(calls);

        Assert.All(results, result => Assert.True(result.IsEligible));
        Assert.Equal(1, dns.PtrCalls);
        Assert.Equal(1, dns.ForwardCalls);
        await validator.GetReadinessAsync(Identity());
        Assert.Equal(1, dns.PtrCalls);
        Assert.Equal(1, dns.ForwardCalls);
    }

    [Fact]
    public async Task Validator_CancelledWaiterDoesNotCancelSharedRefresh()
    {
        var dns = new BlockingDnsResolver();
        var validator = Validator(new ManualTimeProvider(Instant()), dns);
        using var cancellation = new CancellationTokenSource();
        var cancelled = validator.GetReadinessAsync(Identity(), true, cancellation.Token);
        var survivor = validator.GetReadinessAsync(Identity(), true);
        await dns.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        dns.Release.TrySetResult();

        Assert.True((await survivor).IsEligible);
        Assert.Equal(1, dns.PtrCalls);
        Assert.Equal(1, dns.ForwardCalls);
    }

    [Fact]
    public async Task Validator_FailedSingleFlightIsRemovedAndCanRecover()
    {
        var dns = new FailOnceDnsResolver();
        var validator = Validator(new ManualTimeProvider(Instant()), dns);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.GetReadinessAsync(Identity(), true));
        var recovered = await validator.GetReadinessAsync(Identity(), true);

        Assert.True(recovered.IsEligible);
        Assert.Equal(2, dns.PtrCalls);
        Assert.Equal(2, dns.ForwardCalls);
    }

    [Fact]
    public async Task Validator_RefreshesAfterExpiryAndPolicyVersionChange()
    {
        var clock = new ManualTimeProvider(Instant());
        var dns = new CountingDnsResolver();
        var settings = Settings();
        var options = Options.Create(settings);
        var validator = new ForwardConfirmedReverseDnsValidator(
            options, dns, new BoundDiscovery(), new OutboundIdentityReadinessPolicy(options), clock,
            NullLogger<ForwardConfirmedReverseDnsValidator>.Instance);

        await validator.GetReadinessAsync(Identity());
        await validator.GetReadinessAsync(Identity());
        Assert.Equal(1, dns.PtrCalls);
        clock.Advance(TimeSpan.FromMinutes(11));
        await validator.GetReadinessAsync(Identity());
        Assert.Equal(2, dns.PtrCalls);
        settings.OutboundIdentities.DnsReadiness.ValidationPolicyVersion = "test-v2";
        await validator.GetReadinessAsync(Identity());
        Assert.Equal(3, dns.PtrCalls);
    }

    [Fact]
    public async Task Validator_LastKnownGoodGraceStartsWhenPositiveDnsExpires()
    {
        var clock = new ManualTimeProvider(Instant());
        var dns = new MutableDnsResolver();
        var validator = Validator(clock, dns);
        Assert.True((await validator.GetReadinessAsync(Identity())).IsEligible);
        clock.Advance(TimeSpan.FromMinutes(11));
        dns.TemporaryFailure = true;

        var degraded = await validator.GetReadinessAsync(Identity());
        clock.Advance(TimeSpan.FromMinutes(15));
        var expired = await validator.GetReadinessAsync(Identity(), true);

        Assert.True(degraded.IsEligible);
        Assert.True(degraded.IsDegraded);
        Assert.False(expired.IsEligible);
    }

    private static ForwardConfirmedReverseDnsValidator Validator(
        TimeProvider clock,
        IOutboundIdentityDnsResolver dns)
    {
        var options = Options.Create(Settings());
        return new(options, dns, new BoundDiscovery(), new OutboundIdentityReadinessPolicy(options), clock,
            NullLogger<ForwardConfirmedReverseDnsValidator>.Instance);
    }

    private static OutboundIdentityReadinessPolicy Policy(
        OutboundIdentityDnsReadinessMode mode = OutboundIdentityDnsReadinessMode.Enforced,
        ForwardConfirmedReverseDnsValidationMode validationMode =
            ForwardConfirmedReverseDnsValidationMode.StrictOneToOne)
    {
        var settings = Settings();
        settings.OutboundIdentities.DnsReadiness.Mode = mode;
        settings.OutboundIdentities.DnsReadiness.ValidationMode = validationMode;
        return new(Options.Create(settings));
    }

    private static EmailValidationOptions Settings() => new()
    {
        OutboundIdentities = new()
        {
            Enabled = true,
            InterfaceName = "ens19",
            AllowedCidr = "64.182.22.160/28",
            GatewayAddress = "64.182.22.161",
            DnsReadiness = new()
            {
                Enabled = true,
                Mode = OutboundIdentityDnsReadinessMode.Enforced,
                ValidationMode = ForwardConfirmedReverseDnsValidationMode.StrictOneToOne,
                MinimumFreshnessMinutes = 5,
                MaximumFreshnessHours = 24,
                FallbackFreshnessMinutes = 60,
                NegativeCacheMinutes = 5,
                TransientFailureRetrySeconds = 60,
                LastKnownGoodGraceMinutes = 15,
                ValidationPolicyVersion = "test-v1"
            },
            Identities =
            [
                new()
                {
                    IdentityId = "smtp-162",
                    Address = "64.182.22.162",
                    InterfaceName = "ens19",
                    ExpectedPtrHostName = "smtp-162.email.digitalwarehouse.io",
                    EhloHostName = "smtp-162.email.digitalwarehouse.io"
                }
            ]
        }
    };

    private static OutboundIdentity Identity() => new()
    {
        IdentityId = "smtp-162",
        Address = IPAddress.Parse("64.182.22.162"),
        InterfaceName = "ens19",
        ExpectedPtrHostName = "smtp-162.email.digitalwarehouse.io",
        EhloHostName = "smtp-162.email.digitalwarehouse.io",
        Enabled = true
    };

    private static LocalOutboundIdentityBinding Bound() => new("ens19", true, true, true, "ens19");
    private static OutboundIdentityDnsQueryResult Reverse(params string[] names) =>
        new(OutboundIdentityDnsQueryStatus.Success, names, [], TimeSpan.FromMinutes(10));
    private static OutboundIdentityDnsQueryResult Forward(params string[] addresses) =>
        new(OutboundIdentityDnsQueryStatus.Success, [], addresses.Select(IPAddress.Parse).ToArray(),
            TimeSpan.FromMinutes(20));
    private static DateTimeOffset Instant() => new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class BoundDiscovery : ILocalOutboundIdentityDiscovery
    {
        public Task<IReadOnlySet<string>> GetBoundIpv4AddressesAsync(
            string interfaceName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { "64.182.22.162" });
    }

    private sealed class BlockingDnsResolver : IOutboundIdentityDnsResolver
    {
        public int PtrCalls;
        public int ForwardCalls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OutboundIdentityDnsQueryResult> ResolvePtrAsync(
            IPAddress address,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PtrCalls);
            Started.TrySetResult();
            await Release.Task;
            return Reverse("smtp-162.email.digitalwarehouse.io");
        }

        public async Task<OutboundIdentityDnsQueryResult> ResolveIpv4Async(
            string hostName,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ForwardCalls);
            Started.TrySetResult();
            await Release.Task;
            return Forward("64.182.22.162");
        }
    }

    private sealed class CountingDnsResolver : IOutboundIdentityDnsResolver
    {
        public int PtrCalls;
        public int ForwardCalls;

        public Task<OutboundIdentityDnsQueryResult> ResolvePtrAsync(
            IPAddress address,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PtrCalls);
            return Task.FromResult(Reverse("smtp-162.email.digitalwarehouse.io"));
        }

        public Task<OutboundIdentityDnsQueryResult> ResolveIpv4Async(
            string hostName,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ForwardCalls);
            return Task.FromResult(Forward("64.182.22.162"));
        }
    }

    private sealed class FailOnceDnsResolver : IOutboundIdentityDnsResolver
    {
        public int PtrCalls;
        public int ForwardCalls;

        public Task<OutboundIdentityDnsQueryResult> ResolvePtrAsync(
            IPAddress address,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref PtrCalls);
            return call == 1
                ? Task.FromException<OutboundIdentityDnsQueryResult>(
                    new InvalidOperationException("simulated resolver failure"))
                : Task.FromResult(Reverse("smtp-162.email.digitalwarehouse.io"));
        }

        public Task<OutboundIdentityDnsQueryResult> ResolveIpv4Async(
            string hostName,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ForwardCalls);
            return Task.FromResult(Forward("64.182.22.162"));
        }
    }

    private sealed class MutableDnsResolver : IOutboundIdentityDnsResolver
    {
        public bool TemporaryFailure { get; set; }

        public Task<OutboundIdentityDnsQueryResult> ResolvePtrAsync(
            IPAddress address,
            CancellationToken cancellationToken = default) => Task.FromResult(TemporaryFailure
                ? new OutboundIdentityDnsQueryResult(OutboundIdentityDnsQueryStatus.TemporaryFailure, [], [])
                : Reverse("smtp-162.email.digitalwarehouse.io"));

        public Task<OutboundIdentityDnsQueryResult> ResolveIpv4Async(
            string hostName,
            CancellationToken cancellationToken = default) => Task.FromResult(TemporaryFailure
                ? new OutboundIdentityDnsQueryResult(OutboundIdentityDnsQueryStatus.TemporaryFailure, [], [])
                : Forward("64.182.22.162"));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
