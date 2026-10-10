using System.Diagnostics;
using System.Globalization;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace EmailValidation.Core.Tests;

public sealed class MonotonicDomainAndCacheTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OldTopologyWriter_CannotRestoreOldControlsEvenWithLaterBehaviorClock()
    {
        var old = Domain("a", 0, 20);
        var current = DomainIntelligenceMerge.Merge(old, Domain("b", 10, 11));
        var merged = DomainIntelligenceMerge.Merge(current, old);
        Assert.Equal("b", merged.Provider.TopologyFingerprint);
        Assert.Equal(Epoch.AddSeconds(11), merged.CatchAll.ObservedAt);
        Assert.Equal(current.RoutingEvidence, merged.RoutingEvidence);
        Assert.True(merged.ProfileVersion > current.ProfileVersion);
    }

    [Fact]
    public void IndependentClocks_PreserveNewerAuthenticationAndBehaviorWithoutExtendingRouting()
    {
        var current = Domain("a", 10, 20);
        var incoming = Domain("a", 5, 30) with { AuthenticationEvidence = new(Epoch.AddSeconds(40), Epoch.AddSeconds(50)) };
        var merged = DomainIntelligenceMerge.Merge(current, incoming);
        Assert.Equal(current.RoutingEvidence, merged.RoutingEvidence);
        Assert.Equal(current.EvidenceExpiresAt, merged.EvidenceExpiresAt);
        Assert.Equal(incoming.CatchAll, merged.CatchAll);
        Assert.Equal(incoming.AuthenticationEvidence, merged.AuthenticationEvidence);
    }

    [Fact]
    public void RevisitedTopology_CannotReviveControlsFromItsEarlierLifetime()
    {
        var old = Domain("a", 0, 5);
        var moved = DomainIntelligenceMerge.Merge(old, Domain("b", 10, 12));
        var back = DomainIntelligenceMerge.Merge(moved, Domain("a", 20, 5));
        var merged = DomainIntelligenceMerge.Merge(back, old);
        Assert.Equal(CatchAllStatus.NotAttempted, merged.CatchAll.Status);
        Assert.Equal(Epoch.AddSeconds(20), merged.TopologyChangedAt);
    }

    [Fact]
    public void EqualTimeContradictions_RequireFreshEvidenceInEitherOrder()
    {
        var accepted = Domain("a", 0, 5);
        var rejected = accepted with { CatchAll = accepted.CatchAll with
            { Status = CatchAllStatus.NotCatchAll, RecipientBehavior = DomainRecipientBehavior.RecipientSpecific } };
        foreach (var merged in new[] { DomainIntelligenceMerge.Merge(accepted, rejected), DomainIntelligenceMerge.Merge(rejected, accepted) })
        {
            Assert.Equal(CatchAllStatus.Unknown, merged.CatchAll.Status);
            Assert.Equal(Epoch.AddSeconds(5), merged.CatchAll.EvidenceExpiresAt);
        }
    }

    [Fact]
    public void InconclusiveRefreshAttempt_DoesNotOutrankNewerObservedControls()
    {
        var current = Domain("a", 0, 5) with { CatchAll = Domain("a", 0, 5).CatchAll with
            { RefreshAttemptedAt = Epoch.AddSeconds(30), RefreshInconclusive = true } };
        var observed = Domain("a", 0, 20);
        Assert.Equal(observed.CatchAll, DomainIntelligenceMerge.Merge(current, observed).CatchAll);
    }

    [Fact]
    public void LaterTargetEvaluation_PreservesConfirmationWithoutRenewingControlObservation()
    {
        var current = Domain("a", 0, 5);
        var confirmed = current with { CatchAll = current.CatchAll with
        {
            BehaviorEvaluatedAt = Epoch.AddSeconds(10),
            RecipientBehavior = DomainRecipientBehavior.RecipientSpecific,
            Status = CatchAllStatus.NotCatchAll
        } };
        var merged = DomainIntelligenceMerge.Merge(current, confirmed);
        Assert.Equal(confirmed.CatchAll, merged.CatchAll);
        Assert.Equal(Epoch.AddSeconds(5), merged.CatchAll.ObservedAt);
        Assert.Equal(current.RoutingEvidence, merged.RoutingEvidence);
        Assert.Equal(confirmed.CatchAll, DomainIntelligenceMerge.Merge(merged, current).CatchAll);
    }

    [Fact]
    public async Task MillionKeyAndReplacementChurn_HasBoundedEntriesAndBookkeeping()
    {
        var clock = new TestClock(Epoch);
        var options = Options.Create(new EmailValidationOptions { ResultReuse = new() { MemoryCacheSizeLimit = 128 } });
        var cache = new InMemoryValidationResultCache(options, clock);
        var result = new EmailValidationResult { Email = "fixture@example.test", Checks = new() };
        var before = GC.GetTotalMemory(true);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1_000_000; i++) await cache.SetAsync(i.ToString(CultureInfo.InvariantCulture), result, TimeSpan.FromSeconds(10));
        Assert.Equal(128, cache.Count);
        Assert.Equal(cache.Count, cache.BookkeepingCount);
        for (var i = 0; i < 1_000_000; i++)
        {
            var key = (i % 16).ToString(CultureInfo.InvariantCulture);
            await cache.SetAsync(key, result, TimeSpan.FromSeconds(10));
            await cache.RemoveAsync(key);
        }
        Assert.Equal(cache.Count, cache.BookkeepingCount);
        Assert.InRange(cache.Count, 0, 128);
        output.WriteLine($"2M writes + 1M removals: {watch.ElapsedMilliseconds}ms; retained heap delta {GC.GetTotalMemory(true) - before} bytes; entries {cache.Count}, nodes {cache.BookkeepingCount}.");
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(await cache.GetAsync("999999"));
    }

    [Fact]
    public void DomainCache_RespectsCapacityAndAbsoluteExpiry()
    {
        var clock = new TestClock(Epoch);
        var settings = Options.Create(new EmailValidationOptions { Persistence = new() { EvidenceCacheSizeLimit = 2 } });
        var cache = new InMemoryDomainValidationCache(clock, settings);
        for (var i = 0; i < 3; i++) cache.Store(Domain("a", 0, 1) with { Domain = $"{i}.test" }, TimeSpan.FromHours(1));
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet("0.test", out _));
        clock.Advance(TimeSpan.FromHours(2));
        Assert.False(cache.TryGet("2.test", out _));
    }

    [Fact]
    public async Task JsonDomainWriters_MergeAndPersistMonotonically()
    {
        var path = Path.Combine(Path.GetTempPath(), "ev07-json-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new EmailValidationOptions { Persistence = new() { Enabled = true, StoragePath = path, EvidenceCacheSeconds = 0 } });
            var first = new JsonValidationIntelligenceStore(options);
            var second = new JsonValidationIntelligenceStore(options);
            await first.SaveDomainAsync(Domain("b", 20, 25));
            await second.SaveDomainAsync(Domain("a", 0, 50));
            var result = (await first.GetDomainAsync("example.test"))!;
            Assert.Equal("b", result.Provider.TopologyFingerprint);
            Assert.Equal(2, result.ProfileVersion);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private static DomainIntelligence Domain(string topology, int routingSeconds, int behaviorSeconds) => new()
    {
        Domain = "example.test", DomainExists = true,
        Dns = new(DnsStatus.Success, true, [new(0, $"mx.{topology}.test")], false, TimeSpan.Zero),
        Provider = new(MailProvider.GenericSmtp, 1, TopologyFingerprint: topology),
        ObservedAt = Epoch.AddSeconds(routingSeconds), EvidenceExpiresAt = Epoch.AddHours(1),
        RoutingEvidence = new(Epoch.AddSeconds(routingSeconds), Epoch.AddHours(1)),
        CatchAll = new(CatchAllStatus.Unknown, 2, 2, 0, 0)
        { ObservedAt = Epoch.AddSeconds(behaviorSeconds), RecipientBehavior = DomainRecipientBehavior.AcceptAll,
            ReasonCode = CatchAllReasonCode.AcceptAllConfirmed, IndependentObservationCount = 2 }
    };
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
