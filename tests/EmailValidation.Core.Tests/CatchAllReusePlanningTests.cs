using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class CatchAllReusePlanningTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ValidationPolicyVersions Policy = new("1.1.0", "2.2.0", "3.1.0", "1.1.0");

    [Fact]
    public void FreshHighConfidenceCatchAll_UsesPersistedDomainEvidenceAndSkipsSmtpWork()
    {
        var planner = Planner();

        var plan = planner.Build(Domain(), smtpEnabled: true, domainIntelligenceReused: true, Policy, Now);

        Assert.False(plan.RefreshDomainIntelligence);
        Assert.False(plan.PerformCatchAllProbe);
        Assert.False(plan.PerformMailboxProbe);
        Assert.True(plan.UsePersistedCatchAll);
    }

    [Fact]
    public void WeakCatchAllEvidence_RequiresCatchAllAndMailboxProbes()
    {
        var planner = Planner();

        var plan = planner.Build(
            Domain() with { CatchAll = Domain().CatchAll with { Confidence = 0.89 } },
            smtpEnabled: true,
            domainIntelligenceReused: true,
            Policy,
            Now);

        Assert.True(plan.PerformCatchAllProbe);
        Assert.True(plan.PerformMailboxProbe);
        Assert.False(plan.UsePersistedCatchAll);
    }

    [Fact]
    public void ExpiredCatchAllEvidence_RequiresControlledRefreshAndDoesNotSkipMailbox()
    {
        var planner = Planner();
        var stale = Domain() with
        {
            CatchAll = Domain().CatchAll with { ObservedAt = Now.AddDays(-2) }
        };

        var plan = planner.Build(stale, smtpEnabled: true, domainIntelligenceReused: true, Policy, Now);

        Assert.True(plan.PerformCatchAllProbe);
        Assert.True(plan.PerformMailboxProbe);
        Assert.False(plan.UsePersistedCatchAll);
    }

    [Fact]
    public void InconclusiveRefresh_UsesTransientBackoffWithoutTrustingStaleCatchAllForMailboxSkip()
    {
        var planner = Planner();
        var stale = Domain() with
        {
            CatchAll = Domain().CatchAll with
            {
                ObservedAt = Now.AddDays(-2),
                RefreshAttemptedAt = Now.AddMinutes(-1),
                RefreshInconclusive = true
            }
        };

        var plan = planner.Build(stale, smtpEnabled: true, domainIntelligenceReused: true, Policy, Now);

        Assert.False(plan.PerformCatchAllProbe);
        Assert.True(plan.PerformMailboxProbe);
        Assert.False(plan.UsePersistedCatchAll);
    }

    [Fact]
    public void ProviderStrategyVersionChange_RefreshesDomainBeforeReuse()
    {
        var planner = Planner();

        var plan = planner.Build(
            Domain() with { StrategyVersion = "old" },
            smtpEnabled: true,
            domainIntelligenceReused: true,
            Policy,
            Now);

        Assert.True(plan.RefreshDomainIntelligence);
        Assert.True(plan.PerformMailboxProbe);
        Assert.False(plan.UsePersistedCatchAll);
    }

    private static ValidationPlanBuilder Planner() => new(Options.Create(new EmailValidationOptions
    {
        CatchAll = new CatchAllOptions
        {
            Enabled = true,
            CacheMinutes = 1440,
            MinimumReusableConfidence = 0.90
        },
        ResultReuse = new ResultReuseOptions { TransientMinutes = 2 }
    }));

    private static DomainIntelligence Domain() => new()
    {
        Domain = "example.test",
        DomainExists = true,
        Dns = new DnsLookupResult(
            DnsStatus.Success,
            true,
            [new MxRecord(10, "mx.example.test")],
            false,
            TimeSpan.Zero),
        Provider = new ProviderDetectionResult(
            MailProvider.GenericSmtp,
            0.8,
            TopologyFingerprint: "topology-1"),
        CatchAll = new CatchAllDetectionResult(
            CatchAllStatus.LikelyCatchAll,
            2,
            2,
            0,
            0,
            "The domain consistently accepted randomized recipients.",
            0.96)
        {
            ReasonCode = CatchAllReasonCode.RandomRecipientsAccepted,
            ObservedAt = Now.AddMinutes(-10),
            StrategyVersion = Policy.ProviderStrategyVersion
        },
        ObservedAt = Now.AddMinutes(-10),
        EvidenceExpiresAt = Now.AddMinutes(50),
        StrategyVersion = Policy.ProviderStrategyVersion
    };
}
