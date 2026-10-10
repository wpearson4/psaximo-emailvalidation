using EmailValidation.Core;
using System.Diagnostics;
using EmailValidation.Infrastructure;
using EmailValidation.RevalidationProbe;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EmailValidation.IntegrationTests;

[Trait("Category", "MongoIntegration")]
public sealed class DomainAndFleetConcurrencyTests
{
    private static bool Configured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO"));

    [Fact]
    public async Task SeparateProcesses_ShareOneProviderReservation()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        var startGate = Path.Combine(Path.GetTempPath(), "ev12-start-" + Guid.NewGuid().ToString("N"));
        var releaseGate = startGate + "-release";
        using var first = Start("one.test"); using var second = Start("two.test");
        try
        {
            await File.WriteAllTextAsync(startGate, "go");
            var results = await Task.WhenAll(first.StandardOutput.ReadLineAsync(), second.StandardOutput.ReadLineAsync())
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Single(results, result => result == "ACQUIRED");
            Assert.Single(results, result => result == "CONTENDED");
            await File.WriteAllTextAsync(releaseGate, "release");
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, first.ExitCode); Assert.Equal(0, second.ExitCode);
        }
        finally
        {
            if (!first.HasExited) first.Kill(entireProcessTree: true);
            if (!second.HasExited) second.Kill(entireProcessTree: true);
            if (File.Exists(startGate)) File.Delete(startGate);
            if (File.Exists(releaseGate)) File.Delete(releaseGate);
        }
        Process Start(string domain)
        {
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "EmailValidation.IntegrationTests.runtimeconfig.json"),
                "--depsfile", Path.Combine(AppContext.BaseDirectory, "EmailValidation.IntegrationTests.deps.json"), typeof(LeaseProbeMarker).Assembly.Location,
                "fleet", fixture.Options.Value.Persistence.DatabaseName, fixture.Options.Value.Smtp.FleetBudget.Collection, startGate, releaseGate, domain })
                info.ArgumentList.Add(argument);
            return Process.Start(info)!;
        }
    }

    [Theory]
    [InlineData(FleetProbeBudgetMode.Enforced, false)]
    [InlineData(FleetProbeBudgetMode.Observe, true)]
    public async Task StoreFailure_FailsClosedOnlyWhenEnforced(FleetProbeBudgetMode mode, bool expected)
    {
        if (!Configured) return;
        var connection = new MongoClientSettings { Server = new("127.0.0.1", 27918), ServerSelectionTimeout = TimeSpan.FromMilliseconds(150) };
        var options = Options.Create(new EmailValidationOptions
        {
            Persistence = new() { DatabaseName = "ev12_unreachable" },
            Smtp = new() { FleetBudget = new() { Mode = mode, StoreTimeoutSeconds = 1 } }
        });
        using var budget = new MongoFleetSmtpProbeBudget(new MongoClient(connection), options, new ProviderPolicyResolver(options));
        await using var lease = await budget.AcquireAsync(Context("example.test", MailProvider.GenericSmtp));
        Assert.Equal(expected, lease.Acquired);
        Assert.Equal("FleetProbeStoreUnavailable", lease.Reason);
    }

    [Fact]
    public async Task ClientExecutionDeadline_PrecedesServerLeaseExpiry()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        fixture.Options.Value.Smtp.SessionTimeoutSeconds = 1;
        using var budget = fixture.Budget();
        await using var lease = await budget.AcquireAsync(Context("one.test", MailProvider.GoogleWorkspace));
        Assert.True(lease.Acquired);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(9), lease.ExecutionToken));
        using var other = fixture.Budget();
        await using var denied = await other.AcquireAsync(Context("one.test", MailProvider.GoogleWorkspace));
        Assert.False(denied.Acquired); // Server still owns the safety interval after local cancellation.
    }

    [Fact]
    public async Task ForcedStaleWriterRace_PreservesNewTopologyAndImmutableObservation()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        var first = fixture.Store();
        await first.InitializeAsync();
        await first.SaveDomainAsync(Domain("a", 0, 1));
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = fixture.Store(async (attempt, token) =>
        {
            if (attempt != 0) return;
            read.SetResult();
            await resume.Task.WaitAsync(token);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = stale.MergeDomainAsync(Domain("a", 0, 30), timeout.Token);
        await read.Task.WaitAsync(timeout.Token);
        await first.SaveDomainAsync(Domain("b", 10, 11), timeout.Token);
        await first.RecordAsync(new("example.test", ValidationObservationType.MailboxProbe, MailProvider.GenericSmtp,
            "mx.b.test", CatchAllStatus.Unknown, .5, SmtpResponseCategory.TemporaryFailure, DateTimeOffset.UtcNow, 1), timeout.Token);
        resume.SetResult();
        var committed = await pending;
        Assert.Equal("b", committed.Provider.TopologyFingerprint);
        Assert.Equal(Epoch.AddSeconds(11), committed.CatchAll.ObservedAt);
        Assert.Equal(3, committed.ProfileVersion);
        Assert.Single(await first.GetDomainObservationsAsync("example.test"));
        Assert.Equal(committed.ProfileVersion, (await first.GetDomainAsync("example.test"))!.ProfileVersion);
    }

    [Fact]
    public async Task ConcurrentBehaviorAndAuthentication_MergeBothNewComponents()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        var first = fixture.Store();
        await first.SaveDomainAsync(Domain("a", 0, 1));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        async Task Barrier(int attempt, CancellationToken token)
        {
            if (attempt != 0) return;
            if (Interlocked.Increment(ref arrived) == 2) release.SetResult();
            await release.Task.WaitAsync(token);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            fixture.Store(Barrier).MergeDomainAsync(Domain("a", 0, 20), timeout.Token),
            fixture.Store(Barrier).MergeDomainAsync(Domain("a", 0, 1) with
                { AuthenticationEvidence = new(Epoch.AddSeconds(30), Epoch.AddHours(1)) }, timeout.Token));
        var result = (await first.GetDomainAsync("example.test"))!;
        Assert.Equal(Epoch.AddSeconds(20), result.CatchAll.ObservedAt);
        Assert.Equal(Epoch.AddSeconds(30), result.AuthenticationEvidence!.ObservedAt);
    }

    [Fact]
    public async Task LegacyDocument_MigratesAtomicallyAndRetryExhaustionIsBounded()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        var first = fixture.Store();
        await first.SaveDomainAsync(Domain("a", 0, 1));
        var documents = fixture.Database.GetCollection<BsonDocument>(fixture.Options.Value.Persistence.DomainCollection);
        await documents.UpdateOneAsync(new BsonDocument("_id", "example.test"), new BsonDocument("$unset", new BsonDocument("ProfileVersion", 1)));
        var migrated = await first.MergeDomainAsync(Domain("b", 10, 11));
        Assert.Equal(1, migrated.ProfileVersion);
        await documents.UpdateOneAsync(new BsonDocument("_id", "example.test"), new BsonDocument("$unset", new BsonDocument("PayloadJson", 1)));
        Assert.Equal(1, (await first.GetDomainAsync("example.test"))!.ProfileVersion);
        var attempts = 0;
        fixture.Options.Value.Persistence.DomainWriteRetryLimit = 2;
        var contended = fixture.Store(async (_, token) =>
        {
            attempts++;
            await first.SaveDomainAsync(Domain("b", 10, 12 + attempts), token);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => contended.SaveDomainAsync(Domain("a", 0, 40)));
        Assert.Equal(2, attempts);
        Assert.Equal("b", (await first.GetDomainAsync("example.test"))!.Provider.TopologyFingerprint);
    }

    [Fact]
    public async Task DomainCache_UsesCommittedMergeAndSeesOtherWriterWithinFreshnessBound()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        var first = fixture.Store();
        var cache = new PersistentDomainValidationCache(first, fixture.Options, fixture.Clock);
        await cache.StoreAsync(Domain("a", 0, 1), TimeSpan.FromHours(1));
        await fixture.Store().SaveDomainAsync(Domain("b", 10, 11));
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("b", (await cache.GetAsync("example.test"))!.Provider.TopologyFingerprint);
        var result = await cache.StoreMergedAsync(Domain("a", 0, 100), TimeSpan.FromHours(1));
        Assert.Equal("b", result.Provider.TopologyFingerprint);
        Assert.True(cache.TryGet("example.test", out var cached));
        Assert.Equal(result.ProfileVersion, cached!.ProfileVersion);
    }

    [Fact]
    public async Task MailboxCache_SeesOtherWriterAfterAbsoluteExpiry()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        var first = fixture.Store();
        var mailbox = MongoValidationIntelligenceStoreTests.Mailbox();
        await first.SaveMailboxAsync(mailbox);
        await fixture.Store().SaveMailboxAsync(mailbox with { PreviousStatus = EmailValidationStatus.Invalid });
        Assert.Equal(mailbox.PreviousStatus, (await first.GetMailboxAsync(mailbox.NormalizedEmail))!.PreviousStatus);
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(EmailValidationStatus.Invalid, (await first.GetMailboxAsync(mailbox.NormalizedEmail))!.PreviousStatus);
    }

    [Fact]
    public async Task TwoHosts_EnforceAggregateProviderAndDomainCapacityWithoutStarvingOtherProviders()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        using var first = fixture.Budget();
        using var second = fixture.Budget();
        await using var active = await first.AcquireAsync(Context("one.test", MailProvider.GoogleWorkspace));
        Assert.True(active.Acquired);
        await using var sameProvider = await second.AcquireAsync(Context("two.test", MailProvider.GoogleWorkspace));
        Assert.False(sameProvider.Acquired);
        await using var sameDomain = await second.AcquireAsync(Context("one.test", MailProvider.Microsoft365));
        Assert.False(sameDomain.Acquired);
        await using var independent = await second.AcquireAsync(Context("two.test", MailProvider.Microsoft365));
        Assert.True(independent.Acquired); // The failed provider reservation returned its domain capacity.
        await active.DisposeAsync();
        await using var resumed = await second.AcquireAsync(Context("one.test", MailProvider.GoogleWorkspace));
        Assert.True(resumed.Acquired);
    }

    [Fact]
    public async Task ConcurrentSyntheticSessions_NeverExceedFleetProviderLimit()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        using var first = fixture.Budget(); using var second = fixture.Budget();
        await first.InitializeAsync(); await second.InitializeAsync();
        var active = 0; var maximum = 0; var admitted = 0;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 24).Select(async index =>
        {
            await start.Task;
            await using var lease = await (index % 2 == 0 ? first : second).AcquireAsync(Context($"{index}.test", MailProvider.GoogleWorkspace));
            if (!lease.Acquired) return;
            Interlocked.Increment(ref admitted);
            var count = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximum, Math.Max(maximum, count));
            try { await Task.Delay(100, lease.ExecutionToken); }
            finally { Interlocked.Decrement(ref active); }
        }).ToArray();
        start.SetResult(); await Task.WhenAll(attempts);
        Assert.True(admitted > 0);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task ExpiredOwnerRelease_CannotDeleteReplacementReservations()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        using var first = fixture.Budget(); using var second = fixture.Budget();
        await using var abandoned = await first.AcquireAsync(Context("one.test", MailProvider.GoogleWorkspace));
        Assert.True(abandoned.Acquired);
        var documents = fixture.Database.GetCollection<BsonDocument>(fixture.Options.Value.Smtp.FleetBudget.Collection);
        await documents.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            new BsonDocument("$set", new BsonDocument("Holders.$[].ExpiresAt", DateTime.UtcNow.AddMinutes(-1))));
        await using var recovered = await second.AcquireAsync(Context("one.test", MailProvider.GoogleWorkspace));
        Assert.True(recovered.Acquired);
        await abandoned.DisposeAsync();
        await using var contended = await first.AcquireAsync(Context("one.test", MailProvider.GoogleWorkspace));
        Assert.False(contended.Acquired);
    }

    [Theory]
    [InlineData(FleetProbeBudgetMode.Observe, true)]
    [InlineData(FleetProbeBudgetMode.Enforced, false)]
    public async Task ModeControlsAdmission_WhenCapacityIsOccupied(FleetProbeBudgetMode mode, bool expected)
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        using var first = fixture.Budget();
        await using var active = await first.AcquireAsync(Context("one.test", MailProvider.GoogleWorkspace));
        var otherOptions = Options.Create(new EmailValidationOptions
        {
            Persistence = fixture.Options.Value.Persistence,
            Smtp = new() { FleetBudget = new() { Mode = mode, Collection = fixture.Options.Value.Smtp.FleetBudget.Collection,
                GlobalConcurrency = 2, PerProviderConcurrency = 1, PerDomainConcurrency = 1 } }
        });
        using var second = new MongoFleetSmtpProbeBudget(fixture.Client, otherOptions, new ProviderPolicyResolver(otherOptions));
        await using var observed = await second.AcquireAsync(Context("two.test", MailProvider.GoogleWorkspace));
        Assert.Equal(expected, observed.Acquired);
        Assert.Equal("FleetProbeCapacity", observed.Reason);
    }

    private static SmtpThrottleContext Context(string domain, MailProvider provider) => new(domain, "mx.test", provider);
    private static readonly DateTimeOffset Epoch = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static DomainIntelligence Domain(string topology, int routing, int behavior) => new()
    {
        Domain = "example.test", DomainExists = true,
        Dns = new(DnsStatus.Success, true, [new(0, $"mx.{topology}.test")], false, TimeSpan.Zero),
        Provider = new(MailProvider.GenericSmtp, 1, TopologyFingerprint: topology),
        ObservedAt = Epoch.AddSeconds(routing), EvidenceExpiresAt = Epoch.AddDays(2),
        RoutingEvidence = new(Epoch.AddSeconds(routing), Epoch.AddDays(2)),
        CatchAll = new(CatchAllStatus.NotCatchAll, 2, 0, 2, 0)
            { ObservedAt = Epoch.AddSeconds(behavior), RecipientBehavior = DomainRecipientBehavior.RecipientSpecific }
    };
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Epoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public MongoClient Client { get; } = new(Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO"));
        public IOptions<EmailValidationOptions> Options { get; }
        public Clock Clock { get; } = new();
        public IMongoDatabase Database => Client.GetDatabase(Options.Value.Persistence.DatabaseName);
        public Fixture()
        {
            var id = Guid.NewGuid().ToString("N");
            var settings = new EmailValidationOptions();
            settings.Persistence.DatabaseName = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO_DATABASE") ?? "ev0712_test";
            settings.Persistence.DomainCollection = "domains_" + id;
            settings.Persistence.MailboxCollection = "mailboxes_" + id;
            settings.Smtp.FleetBudget = new() { Mode = FleetProbeBudgetMode.Enforced, Collection = "leases_" + id,
                GlobalConcurrency = 2, PerProviderConcurrency = 1, PerDomainConcurrency = 1 };
            Options = Microsoft.Extensions.Options.Options.Create(settings);
        }
        public MongoValidationIntelligenceStore Store(Func<int, CancellationToken, Task>? hook = null) => new(Client, Options,
            new ValidationPersistenceMetrics(), NullLogger<MongoValidationIntelligenceStore>.Instance, Clock) { BeforeDomainWrite = hook };
        public MongoFleetSmtpProbeBudget Budget() => new(Client, Options, new ProviderPolicyResolver(Options));
        public async ValueTask DisposeAsync()
        {
            foreach (var name in new[] { Options.Value.Persistence.DomainCollection, Options.Value.Persistence.MailboxCollection, Options.Value.Smtp.FleetBudget.Collection })
                await Database.DropCollectionAsync(name);
        }
    }
}
