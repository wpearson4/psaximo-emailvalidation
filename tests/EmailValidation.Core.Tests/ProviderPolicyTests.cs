using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class ProviderPolicyTests
{
    [Fact]
    public void SuppliedProviderPolicies_BindAllProperties()
    {
        var values = new Dictionary<string, string?>
        {
            ["EmailValidation:Scheduling:ProviderPolicies:Yahoo:PerProviderConcurrency"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Yahoo:DelayMilliseconds"] = "4000",
            ["EmailValidation:Scheduling:ProviderPolicies:Yahoo:PolicyBlockCooldownMinutes"] = "60",
            ["EmailValidation:Scheduling:ProviderPolicies:Yahoo:MaxRetries"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:MicrosoftConsumer:PerProviderConcurrency"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:MicrosoftConsumer:DelayMilliseconds"] = "4000",
            ["EmailValidation:Scheduling:ProviderPolicies:MicrosoftConsumer:PolicyBlockCooldownMinutes"] = "90",
            ["EmailValidation:Scheduling:ProviderPolicies:MicrosoftConsumer:MaxRetries"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Microsoft365:PerProviderConcurrency"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Microsoft365:DelayMilliseconds"] = "3000",
            ["EmailValidation:Scheduling:ProviderPolicies:Microsoft365:PolicyBlockCooldownMinutes"] = "60",
            ["EmailValidation:Scheduling:ProviderPolicies:Microsoft365:MaxRetries"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Google:PerProviderConcurrency"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Google:DelayMilliseconds"] = "3000",
            ["EmailValidation:Scheduling:ProviderPolicies:Google:PolicyBlockCooldownMinutes"] = "45",
            ["EmailValidation:Scheduling:ProviderPolicies:Google:MaxRetries"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:AppleICloud:PerProviderConcurrency"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:AppleICloud:DelayMilliseconds"] = "4000",
            ["EmailValidation:Scheduling:ProviderPolicies:AppleICloud:PolicyBlockCooldownMinutes"] = "60",
            ["EmailValidation:Scheduling:ProviderPolicies:AppleICloud:MaxRetries"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Comcast:PerProviderConcurrency"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Comcast:DelayMilliseconds"] = "3000",
            ["EmailValidation:Scheduling:ProviderPolicies:Comcast:PolicyBlockCooldownMinutes"] = "45",
            ["EmailValidation:Scheduling:ProviderPolicies:Comcast:MaxRetries"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Proton:PerProviderConcurrency"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Proton:DelayMilliseconds"] = "4000",
            ["EmailValidation:Scheduling:ProviderPolicies:Proton:PolicyBlockCooldownMinutes"] = "60",
            ["EmailValidation:Scheduling:ProviderPolicies:Proton:MaxRetries"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Zoho:PerProviderConcurrency"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Zoho:DelayMilliseconds"] = "2500",
            ["EmailValidation:Scheduling:ProviderPolicies:Zoho:PolicyBlockCooldownMinutes"] = "45",
            ["EmailValidation:Scheduling:ProviderPolicies:Zoho:MaxRetries"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Fastmail:PerProviderConcurrency"] = "1",
            ["EmailValidation:Scheduling:ProviderPolicies:Fastmail:DelayMilliseconds"] = "2500",
            ["EmailValidation:Scheduling:ProviderPolicies:Fastmail:PolicyBlockCooldownMinutes"] = "45",
            ["EmailValidation:Scheduling:ProviderPolicies:Fastmail:MaxRetries"] = "1"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new EmailValidationOptions();

        configuration.GetSection("EmailValidation").Bind(options);

        AssertPolicy(options.Scheduling.ProviderPolicies["Yahoo"], 1, 4000, 60, 1);
        AssertPolicy(options.Scheduling.ProviderPolicies["MicrosoftConsumer"], 1, 4000, 90, 1);
        AssertPolicy(options.Scheduling.ProviderPolicies["Microsoft365"], 1, 3000, 60, 1);
        AssertPolicy(options.Scheduling.ProviderPolicies["Google"], 1, 3000, 45, 1);
        AssertPolicy(options.Scheduling.ProviderPolicies["AppleICloud"], 1, 4000, 60, 1);
        AssertPolicy(options.Scheduling.ProviderPolicies["Comcast"], 1, 3000, 45, 1);
        AssertPolicy(options.Scheduling.ProviderPolicies["Proton"], 1, 4000, 60, 1);
        AssertPolicy(options.Scheduling.ProviderPolicies["Zoho"], 1, 2500, 45, 1);
        AssertPolicy(options.Scheduling.ProviderPolicies["Fastmail"], 1, 2500, 45, 1);
    }

    [Fact]
    public void Microsoft365Mx_NormalizesToMicrosoft365Policy()
    {
        var detector = new MailProviderDetector();
        var detected = detector.Detect([new MxRecord(10, "tenant.mail.protection.outlook.com")]);
        var resolver = Resolver(new Dictionary<string, ProviderPolicyOptions>
        {
            ["Microsoft365"] = Policy(1, 3000, 60, 1)
        });

        Assert.Equal(MailProvider.Microsoft365, detected);
        Assert.Equal("Microsoft365", resolver.Resolve(detected).ProviderKey);
    }

    [Theory]
    [InlineData("hotmail-com.olc.protection.outlook.com")]
    [InlineData("outlook-com.olc.protection.outlook.com")]
    [InlineData("msn-com.olc.protection.outlook.com")]
    public void MicrosoftConsumerMx_NormalizesToConsumerPolicy(string mxHost)
    {
        var detector = new MailProviderDetector();
        var detected = detector.Detect([new MxRecord(10, mxHost)]);
        var resolver = Resolver(new Dictionary<string, ProviderPolicyOptions>
        {
            ["MicrosoftConsumer"] = Policy(1, 4000, 90, 1)
        });

        Assert.Equal(MailProvider.MicrosoftConsumer, detected);
        Assert.Equal("MicrosoftConsumer", resolver.Resolve(detected).ProviderKey);
    }

    [Fact]
    public void YahooMx_IsDetectedAndUsesYahooPolicy()
    {
        var detected = new MailProviderDetector().Detect(
            [new MxRecord(10, "mta7.am0.yahoodns.net")]);
        var resolver = Resolver(new Dictionary<string, ProviderPolicyOptions>
        {
            ["Yahoo"] = Policy(1, 2500, 30, 1)
        });

        Assert.Equal(MailProvider.Yahoo, detected);
        Assert.Equal("Yahoo", resolver.Resolve(detected).ProviderKey);
    }

    [Theory]
    [InlineData("mx-aol.mail.gm0.yahoodns.net")]
    [InlineData("mx-att.mail.am0.yahoodns.net")]
    [InlineData("mta5.am0.yahoodns.net")]
    public void YahooHostedConsumerFamilies_ShareYahooPolicy(string mxHost)
    {
        var detected = new MailProviderDetector().Detect([new MxRecord(10, mxHost)]);

        Assert.Equal(MailProvider.Yahoo, detected);
        Assert.Equal("Yahoo", Resolver(new Dictionary<string, ProviderPolicyOptions>
        {
            ["Yahoo"] = Policy(1, 4000, 60, 1)
        }).Resolve(detected).ProviderKey);
    }

    [Theory]
    [InlineData(MailProvider.AppleICloud, "AppleICloud")]
    [InlineData(MailProvider.Comcast, "Comcast")]
    [InlineData(MailProvider.Proton, "Proton")]
    [InlineData(MailProvider.Zoho, "Zoho")]
    [InlineData(MailProvider.Fastmail, "Fastmail")]
    public void HostedProviders_ResolveToIndependentPolicyKeys(MailProvider provider, string expectedKey)
    {
        Assert.Equal(expectedKey, Resolver([]).Resolve(provider).ProviderKey);
    }

    [Fact]
    public void MissingPolicy_UsesConfiguredDefault()
    {
        var resolver = Resolver([], Policy(3, 750, 12, 2));

        var result = resolver.Resolve(MailProvider.Proofpoint);

        Assert.Equal("Proofpoint", result.ProviderKey);
        Assert.Equal(3, result.PerProviderConcurrency);
        Assert.Equal(750, result.DelayMilliseconds);
        Assert.Equal(12, result.PolicyBlockCooldownMinutes);
        Assert.Equal(2, result.MaxRetries);
    }

    [Fact]
    public void InvalidProviderPolicy_FailsWithoutCoercion()
    {
        var options = ValidBaseOptions();
        options.Scheduling.ProviderPolicies["Microsoft"] = new ProviderPolicyOptions
        {
            PerProviderConcurrency = 0,
            DelayMilliseconds = -1,
            PolicyBlockCooldownMinutes = -1,
            MaxRetries = -1
        };

        var result = new EmailValidationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("PerProviderConcurrency", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("DelayMilliseconds", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("PolicyBlockCooldownMinutes", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("MaxRetries", StringComparison.Ordinal));
    }

    [Fact]
    public void RetryLimit_UsesLowerProviderOrGlobalBudget()
    {
        var provider = new ProviderPolicy("Yahoo", 1, 0, 30, 1);

        Assert.Equal(1, SmtpMailboxProbe.EffectiveRetryLimit(4, provider));
        Assert.Equal(0, SmtpMailboxProbe.EffectiveRetryLimit(0, provider));
    }

    [Fact]
    public async Task ProviderConcurrency_AppliesAcrossThreeDomains()
    {
        using var throttle = Throttle(Policies(("Microsoft", Policy(1, 0, 60, 1))));
        var first = await throttle.AcquireAsync(Context("one.test", MailProvider.Microsoft365));
        var secondTask = throttle.AcquireAsync(Context("two.test", MailProvider.Microsoft365)).AsTask();
        var thirdTask = throttle.AcquireAsync(Context("three.test", MailProvider.Microsoft365)).AsTask();

        Assert.False(secondTask.IsCompleted);
        Assert.False(thirdTask.IsCompleted);
        await first.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(thirdTask.IsCompleted);
        await second.DisposeAsync();
        await (await thirdTask.WaitAsync(TimeSpan.FromSeconds(1))).DisposeAsync();
    }

    [Fact]
    public async Task DifferentProviders_CanHoldConcurrentLeases()
    {
        using var throttle = Throttle(Policies(
            ("Microsoft", Policy(1, 0, 60, 1)),
            ("Yahoo", Policy(1, 0, 30, 1)),
            ("Google", Policy(1, 0, 30, 1))));

        var microsoft = await throttle.AcquireAsync(Context("one.test", MailProvider.Microsoft365));
        var yahoo = await throttle.AcquireAsync(Context("two.test", MailProvider.Yahoo));
        var google = await throttle.AcquireAsync(Context("three.test", MailProvider.GoogleWorkspace));

        Assert.Equal(1, throttle.GetProviderState(MailProvider.Microsoft365)!.ActiveCount);
        Assert.Equal(1, throttle.GetProviderState(MailProvider.Yahoo)!.ActiveCount);
        Assert.Equal(1, throttle.GetProviderState(MailProvider.GoogleWorkspace)!.ActiveCount);
        await microsoft.DisposeAsync();
        await yahoo.DisposeAsync();
        await google.DisposeAsync();
    }

    [Fact]
    public async Task ProviderDelay_UsesFakeTimeAcrossDomains()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var throttle = Throttle(
            Policies(("Google", Policy(1, 2000, 30, 1))), time);
        await (await throttle.AcquireAsync(Context("one.test", MailProvider.GoogleWorkspace))).DisposeAsync();

        var waiting = throttle.AcquireAsync(Context("two.test", MailProvider.GoogleWorkspace)).AsTask();
        await WaitForAsync(() => throttle.GetSnapshot().ProviderPacingWaits > 0);
        time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));

        await (await waiting.WaitAsync(TimeSpan.FromSeconds(1))).DisposeAsync();
    }

    [Fact]
    public async Task PolicyBlock_ReturnsImmediatelyWithoutProbe_WhileYahooContinues()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        using var throttle = Throttle(Policies(
            ("Microsoft", Policy(1, 0, 60, 1)),
            ("Yahoo", Policy(1, 0, 30, 1))), time);
        var microsoftContext = Context("microsoft.test", MailProvider.Microsoft365);
        var blocked = PolicyBlock(MailProvider.Microsoft365);
        Assert.False(SmtpSenderFailureClassifier.ShouldTryAlternate(blocked));
        Assert.Equal(ValidationFailureScope.Provider, SmtpSenderFailureClassifier.Scope(blocked));
        await using (var lease = await throttle.AcquireAsync(microsoftContext))
            throttle.RecordOutcome(microsoftContext, blocked);

        var state = throttle.GetProviderState(MailProvider.Microsoft365)!;
        Assert.Equal(ProviderCircuitState.Open, state.CircuitState);
        Assert.Equal(start.AddMinutes(60), state.CooldownUntil);
        var availability = throttle.GetAvailability(
            Context("another.test", MailProvider.Microsoft365));
        Assert.False(availability.CanProbe);
        Assert.Equal(start.AddMinutes(60), availability.RetryAfter);
        var microsoftSkipped = await throttle.AcquireAsync(
            Context("another.test", MailProvider.Microsoft365));

        await (await throttle.AcquireAsync(Context("yahoo.test", MailProvider.Yahoo))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(1))).DisposeAsync();
        Assert.False(microsoftSkipped.Acquired);
        Assert.Equal(start.AddMinutes(60), microsoftSkipped.RetryAfter);
        await microsoftSkipped.DisposeAsync();
    }

    [Fact]
    public async Task MailboxProbe_DuringProviderCooldown_ReturnsBlockedWithoutSelectingSender()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var settings = new EmailValidationOptions
        {
            Smtp = new SmtpOptions
            {
                GlobalConcurrency = 2,
                PerDomainConcurrency = 1,
                PerProviderConcurrency = 1,
                DelayBetweenDomainRequestsMilliseconds = 0
            },
            Scheduling = Policies(("Microsoft", Policy(1, 0, 60, 1)))
        };
        var options = Options.Create(settings);
        var resolver = new ProviderPolicyResolver(options);
        using var throttle = new DomainSmtpProbeThrottle(
            options, time, new DomainPacingJitter(), new DomainBackoffPolicy(options), resolver,
            NullLogger<DomainSmtpProbeThrottle>.Instance);
        var context = Context("blocked.test", MailProvider.Microsoft365);
        await using (var initial = await throttle.AcquireAsync(context))
            throttle.RecordOutcome(context, PolicyBlock(MailProvider.Microsoft365));
        var senderPool = new NoCallSenderPool();
        var probe = new SmtpMailboxProbe(
            options,
            NullLogger<SmtpMailboxProbe>.Instance,
            throttle,
            new SmtpResponseClassifier(),
            senderPool,
            new ProbeSenderAffinityStore(time, options),
            new SmtpSessionBudget(),
            resolver);

        var result = await probe.ProbeAsync(
            "blocked.test", "person@blocked.test", MailProvider.Microsoft365);

        Assert.Equal(SmtpMailboxStatus.Blocked, result.Status);
        Assert.Equal(SmtpResponseCategory.VerificationBlocked, result.Evidence!.Category);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(0, senderPool.Selections);
    }

    [Fact]
    public async Task HalfOpenSuccess_ClosesCircuitAndAllowsOnlyOneProbe()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var throttle = Throttle(
            Policies(("Microsoft", Policy(2, 0, 60, 1))), time);
        var context = Context("one.test", MailProvider.Microsoft365);
        await using (var initial = await throttle.AcquireAsync(context))
            throttle.RecordOutcome(context, PolicyBlock(MailProvider.Microsoft365));
        time.Advance(TimeSpan.FromMinutes(60));

        var halfOpen = await throttle.AcquireAsync(Context("two.test", MailProvider.Microsoft365));
        Assert.Equal(ProviderCircuitState.HalfOpen,
            throttle.GetProviderState(MailProvider.Microsoft365)!.CircuitState);
        var second = throttle.AcquireAsync(Context("three.test", MailProvider.Microsoft365)).AsTask();
        Assert.False(second.IsCompleted);
        throttle.RecordOutcome(Context("two.test", MailProvider.Microsoft365), Success(MailProvider.Microsoft365));
        await halfOpen.DisposeAsync();
        await (await second.WaitAsync(TimeSpan.FromSeconds(1))).DisposeAsync();

        Assert.Equal(ProviderCircuitState.Closed,
            throttle.GetProviderState(MailProvider.Microsoft365)!.CircuitState);
        Assert.Equal(1, throttle.GetSnapshot().ProviderResumptions);
    }

    [Fact]
    public async Task HalfOpenPolicyBlock_ReopensFullCooldown()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        using var throttle = Throttle(
            Policies(("Microsoft", Policy(1, 0, 60, 1))), time);
        var firstContext = Context("one.test", MailProvider.Microsoft365);
        await using (var initial = await throttle.AcquireAsync(firstContext))
            throttle.RecordOutcome(firstContext, PolicyBlock(MailProvider.Microsoft365));
        time.Advance(TimeSpan.FromMinutes(60));
        var secondContext = Context("two.test", MailProvider.Microsoft365);

        await using (var halfOpen = await throttle.AcquireAsync(secondContext))
            throttle.RecordOutcome(secondContext, PolicyBlock(MailProvider.Microsoft365));

        var state = throttle.GetProviderState(MailProvider.Microsoft365)!;
        Assert.Equal(ProviderCircuitState.Open, state.CircuitState);
        Assert.Equal(start.AddMinutes(120), state.CooldownUntil);
    }

    [Fact]
    public async Task RecipientRejection_DoesNotOpenProviderCircuit()
    {
        using var throttle = Throttle(
            Policies(("Microsoft", Policy(1, 0, 60, 1))));
        var context = Context("one.test", MailProvider.Microsoft365);

        await using (var lease = await throttle.AcquireAsync(context))
            throttle.RecordOutcome(context, RecipientRejected(MailProvider.Microsoft365));

        var state = throttle.GetProviderState(MailProvider.Microsoft365)!;
        Assert.Equal(ProviderCircuitState.Closed, state.CircuitState);
        Assert.Null(state.CooldownUntil);
    }

    private static ProviderPolicyResolver Resolver(
        Dictionary<string, ProviderPolicyOptions> policies,
        ProviderPolicyOptions? fallback = null) => new(Options.Create(new EmailValidationOptions
        {
            Scheduling = new SchedulingOptions
            {
                DefaultProviderPolicy = fallback,
                ProviderPolicies = policies
            }
        }));

    private static DomainSmtpProbeThrottle Throttle(
        SchedulingOptions scheduling,
        TimeProvider? time = null)
    {
        var options = Options.Create(new EmailValidationOptions
        {
            Smtp = new SmtpOptions
            {
                GlobalConcurrency = 4,
                PerDomainConcurrency = 1,
                PerProviderConcurrency = 2,
                DelayBetweenDomainRequestsMilliseconds = 0
            },
            Scheduling = scheduling
        });
        return new DomainSmtpProbeThrottle(
            options,
            time ?? TimeProvider.System,
            new DomainPacingJitter(),
            new DomainBackoffPolicy(options),
            new ProviderPolicyResolver(options),
            NullLogger<DomainSmtpProbeThrottle>.Instance);
    }

    private static SchedulingOptions Policies(params (string Name, ProviderPolicyOptions Policy)[] entries) => new()
    {
        GlobalConcurrency = 4,
        PerDomainConcurrency = 1,
        DomainMinIntervalMilliseconds = 0,
        DomainIntervalJitterMilliseconds = 0,
        ProviderPolicies = entries.ToDictionary(entry => entry.Name, entry => entry.Policy,
            StringComparer.OrdinalIgnoreCase)
    };

    private static ProviderPolicyOptions Policy(int concurrency, int delay, int cooldown, int retries) => new()
    {
        PerProviderConcurrency = concurrency,
        DelayMilliseconds = delay,
        PolicyBlockCooldownMinutes = cooldown,
        MaxRetries = retries
    };

    private static SmtpThrottleContext Context(string domain, MailProvider provider) =>
        new(domain, $"mx.{domain}", provider);

    private static SmtpProbeResult PolicyBlock(MailProvider provider)
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 550, "5.7.1", SmtpResponseCategory.VerificationBlocked,
            SmtpResponseTextClassification.VerificationUnavailable, 1, provider,
            "mx.test", 1, DateTimeOffset.UtcNow, "550 blocked by policy");
        var session = new SmtpSessionEvidence(
            SmtpCommand.RcptTo,
            [
                new(SmtpCommand.MailFrom, 250, "2.1.0", SmtpResponseCategory.Accepted,
                    SmtpResponseTextClassification.Success, TimeSpan.Zero),
                new(SmtpCommand.RcptTo, 550, "5.7.1", SmtpResponseCategory.VerificationBlocked,
                    SmtpResponseTextClassification.VerificationUnavailable, TimeSpan.Zero)
            ],
            "mx.test", TimeSpan.Zero, "probe@example.test");
        return new SmtpProbeResult(
            SmtpMailboxStatus.Blocked, 550, "550 blocked by policy", TimeSpan.Zero,
            Evidence: evidence, SessionEvidence: session);
    }

    private static SmtpProbeResult Success(MailProvider provider) => Result(
        provider, 250, SmtpResponseCategory.Accepted,
        SmtpResponseTextClassification.Success, SmtpMailboxStatus.Accepted, "250 accepted");

    private static SmtpProbeResult RecipientRejected(MailProvider provider) => Result(
        provider, 550, SmtpResponseCategory.RecipientRejected,
        SmtpResponseTextClassification.RecipientDoesNotExist, SmtpMailboxStatus.Rejected,
        "550 5.1.1 recipient does not exist");

    private static SmtpProbeResult Result(
        MailProvider provider,
        int code,
        SmtpResponseCategory category,
        SmtpResponseTextClassification textClassification,
        SmtpMailboxStatus status,
        string response)
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, code, null, category, textClassification, 1, provider,
            "mx.test", 1, DateTimeOffset.UtcNow, response);
        var session = new SmtpSessionEvidence(
            category == SmtpResponseCategory.Accepted ? null : SmtpCommand.RcptTo,
            [
                new(SmtpCommand.MailFrom, 250, "2.1.0", SmtpResponseCategory.Accepted,
                    SmtpResponseTextClassification.Success, TimeSpan.Zero),
                new(SmtpCommand.RcptTo, code, null, category, textClassification, TimeSpan.Zero)
            ],
            "mx.test", TimeSpan.Zero, "probe@example.test");
        return new SmtpProbeResult(status, code, response, TimeSpan.Zero,
            Evidence: evidence, SessionEvidence: session);
    }

    private static void AssertPolicy(
        ProviderPolicyOptions policy,
        int concurrency,
        int delay,
        int cooldown,
        int retries)
    {
        Assert.Equal(concurrency, policy.PerProviderConcurrency);
        Assert.Equal(delay, policy.DelayMilliseconds);
        Assert.Equal(cooldown, policy.PolicyBlockCooldownMinutes);
        Assert.Equal(retries, policy.MaxRetries);
    }

    private static EmailValidationOptions ValidBaseOptions() => new()
    {
        ProbeSenderSource = new ProbeSenderSourceOptions
        {
            Provider = "Elasticsearch",
            Endpoint = "http://localhost:9200",
            Index = "senders",
            EmailField = "email",
            QueryLimit = 100,
            RefreshThreshold = 10,
            QueryJson = "{}"
        }
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Yield();
        Assert.True(condition());
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync) return _now;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
                timer.ChangeUnderLock(dueTime, period);
            }
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_sync)
            {
                _now += duration;
                foreach (var timer in _timers.ToArray())
                    timer.CollectDueUnderLock(_now, callbacks);
            }
            foreach (var callback in callbacks) callback.Callback(callback.State);
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private DateTimeOffset? _dueAt;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    if (_disposed) return false;
                    ChangeUnderLock(dueTime, period);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._sync)
                {
                    _disposed = true;
                    _dueAt = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void ChangeUnderLock(TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now.Add(dueTime);
            }

            internal void CollectDueUnderLock(
                DateTimeOffset current,
                List<(TimerCallback Callback, object? State)> callbacks)
            {
                if (_disposed || _dueAt is null || _dueAt > current) return;
                callbacks.Add((callback, state));
                _dueAt = _period == Timeout.InfiniteTimeSpan ? null : current.Add(_period);
            }
        }
    }

    private sealed class NoCallSenderPool : IProbeSenderPool
    {
        public int Selections { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ProbeSenderSelection?> GetSenderAsync(
            ProbeSenderContext context,
            CancellationToken cancellationToken = default)
        {
            Selections++;
            return Task.FromResult<ProbeSenderSelection?>(new("probe@example.test", ProbeSenderCandidateState.Healthy));
        }

        public Task RecordOutcomeAsync(
            ProbeSenderOutcome outcome,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ProbeSenderPoolSnapshot GetSnapshot() => new(
            "test", "test", 1, 1, 1, 0, null, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero);
    }
}
