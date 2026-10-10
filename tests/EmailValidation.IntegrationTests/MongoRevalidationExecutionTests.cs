using System.Diagnostics;
using System.Globalization;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using EmailValidation.RevalidationProbe;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EmailValidation.IntegrationTests;

public sealed class MongoRevalidationExecutionTests
{
    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task ServerTime_FencesExpiredLeaseEvenWhenWorkerClockIsBehind()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        var initial = fixture.Initial();
        await fixture.Store.TrySaveAsync(initial, 0);
        var acquired = (await fixture.Store.TryAcquireExecutionAsync(initial.ValidationId, 1, 2, "owner", TimeSpan.FromMinutes(1)))!;
        await fixture.Database.GetCollection<BsonDocument>(fixture.Collection).UpdateOneAsync(
            new BsonDocument("_id", initial.ValidationId),
            new BsonDocument("$set", new BsonDocument("ExecutionLeaseExpiresAtUtc", DateTime.UtcNow.AddSeconds(-1))));
        fixture.Clock.Advance(TimeSpan.FromSeconds(-30));
        Assert.False((await fixture.Store.TrySaveExecutionAsync(Final(acquired), acquired.Version, acquired.ExecutionLease!)).Applied);
        Assert.False(await fixture.Store.RenewExecutionAsync(initial.ValidationId, acquired.ExecutionLease!, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task SeparateProcesses_CannotAcquireTheSameExecution()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var initial = fixture.Initial();
        await fixture.Store.TrySaveAsync(initial, 0);
        var gate = Path.Combine(Path.GetTempPath(), $"ev04-gate-{Guid.NewGuid():N}");
        using var first = Start("process-a");
        using var second = Start("process-b");
        try
        {
            await File.WriteAllTextAsync(gate, "start");
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
            var errors = await Task.WhenAll(first.StandardError.ReadToEndAsync(), second.StandardError.ReadToEndAsync());
            Assert.True(first.ExitCode == 0 && second.ExitCode == 0, string.Join('\n', errors));
            var results = await Task.WhenAll(first.StandardOutput.ReadToEndAsync(), second.StandardOutput.ReadToEndAsync());
            Assert.Single(results, result => result.Trim() == "ACQUIRED");
            Assert.Single(results, result => result.Trim() == "CONTENDED");
            Assert.Equal(1, (await fixture.Store.GetAsync(initial.ValidationId))!.ExecutionFence);
        }
        finally
        {
            if (!first.HasExited) first.Kill(entireProcessTree: true);
            if (!second.HasExited) second.Kill(entireProcessTree: true);
            if (File.Exists(gate)) File.Delete(gate);
        }

        Process Start(string owner)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "EmailValidation.IntegrationTests.runtimeconfig.json"),
                "--depsfile", Path.Combine(AppContext.BaseDirectory, "EmailValidation.IntegrationTests.deps.json"), typeof(LeaseProbeMarker).Assembly.Location,
                fixture.Database.DatabaseNamespace.DatabaseName, fixture.Collection, initial.ValidationId, owner, gate,
                fixture.Clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) }) start.ArgumentList.Add(value);
            return Process.Start(start)!;
        }
    }

    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task RenewalRecoveryAndFencedCommit_AreAtomicAcrossStoreInstances()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var initial = fixture.Initial();
        await fixture.Store.TrySaveAsync(initial, 0);
        var other = fixture.NewStore();
        var acquired = (await fixture.Store.TryAcquireExecutionAsync(initial.ValidationId, 1, 2, "owner-a", TimeSpan.FromSeconds(1)))!;
        Assert.Null(await other.TryAcquireExecutionAsync(initial.ValidationId, acquired.Version, 2, "owner-b", TimeSpan.FromSeconds(1)));
        Assert.False((await other.TrySaveAsync(acquired with { Version = acquired.Version + 1 }, acquired.Version)).Applied);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(await other.RenewExecutionAsync(initial.ValidationId, acquired.ExecutionLease!, TimeSpan.FromSeconds(1)));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Empty(await fixture.Store.RecoverOverdueAsync(1, TimeSpan.FromMinutes(15)));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(await other.RenewExecutionAsync(initial.ValidationId, acquired.ExecutionLease!, TimeSpan.FromSeconds(1)));
        Assert.False((await other.TrySaveExecutionAsync(Final(acquired), acquired.Version, acquired.ExecutionLease!)).Applied);
        await Task.Delay(TimeSpan.FromMilliseconds(1600));
        Assert.Equal(initial.ValidationId, Assert.Single(await fixture.Store.RecoverOverdueAsync(1, TimeSpan.FromMinutes(15))));
        var recovered = (await other.GetAsync(initial.ValidationId))!;
        Assert.Equal(1, recovered.AttemptNumber);
        Assert.Equal(2, recovered.DispatchGeneration);
        Assert.Equal(2, recovered.PendingRevalidation!.Message.AttemptNumber);
        var next = (await other.TryAcquireExecutionAsync(initial.ValidationId, recovered.Version, 2, "owner-b", TimeSpan.FromSeconds(10)))!;
        Assert.Equal(2, next.ExecutionFence);
        // Even a stale writer that fetched the latest CAS version cannot bypass the fence.
        Assert.False((await fixture.Store.TrySaveExecutionAsync(Final(next), next.Version, acquired.ExecutionLease!)).Applied);
        Assert.True((await other.TrySaveExecutionAsync(Final(next), next.Version, next.ExecutionLease!)).Applied);
        Assert.False(await other.RenewExecutionAsync(initial.ValidationId, next.ExecutionLease!, TimeSpan.FromSeconds(1)));
        Assert.Equal(ValidationResultState.Final, (await fixture.NewStore().GetAsync(initial.ValidationId))!.ResultState);
    }

    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task PublishCrash_ReplaysSameGeneration_AndOldAcknowledgementCannotClearDeferral()
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var initial = fixture.Initial();
        var message = Message(initial);
        initial = initial with { PendingRevalidation = new(message, fixture.Clock.GetUtcNow(), message.ScheduledRetryAt) };
        await fixture.Store.TrySaveAsync(initial, 0);
        var first = await fixture.Store.TryClaimAsync(initial.ValidationId, TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        var replay = await fixture.NewStore().TryClaimAsync(initial.ValidationId, TimeSpan.FromSeconds(5));
        Assert.Equal(first!.Message.MessageId, replay!.Message.MessageId);
        var current = (await fixture.Store.GetAsync(initial.ValidationId))!;
        var deferred = current with
        {
            Version = current.Version + 1, DispatchGeneration = 2,
            PendingRevalidation = new(message with { DispatchGeneration = 2 }, fixture.Clock.GetUtcNow(), message.ScheduledRetryAt)
        };
        Assert.True((await fixture.Store.TrySaveAsync(deferred, current.Version)).Applied);
        Assert.False(await fixture.Store.MarkScheduledAsync(initial.ValidationId, first.Message.MessageId,
            new(true, first.Message.MessageId, first.ScheduledAt)));
        await fixture.Store.ReleaseAsync(initial.ValidationId, first.Message.MessageId, "old-publisher");
        var newDispatch = (await fixture.Store.TryClaimAsync(initial.ValidationId, TimeSpan.FromSeconds(5)))!;
        Assert.NotEqual(first.Message.MessageId, newDispatch.Message.MessageId);
        Assert.True(await fixture.Store.MarkScheduledAsync(initial.ValidationId, newDispatch.Message.MessageId,
            new(true, newDispatch.Message.MessageId, newDispatch.ScheduledAt)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "MongoIntegration")]
    public async Task RecoveryMetadataMigration_FindsEligibleLegacyRecordsWithoutScanningUnrelatedStates(bool abandoned)
    {
        if (!Configured) return;
        await using var fixture = new Fixture();
        var initial = fixture.Initial() with { NextRetryAt = fixture.Clock.GetUtcNow().AddHours(-1) };
        if (abandoned) initial = initial with
        {
            LifecycleState = ValidationLifecycleState.Revalidating, AttemptNumber = 2,
            PendingRevalidation = new(Message(initial), initial.FirstValidatedAt, initial.NextRetryAt!.Value)
        };
        await fixture.Store.TrySaveAsync(initial, 0);
        var raw = fixture.Database.GetCollection<BsonDocument>(fixture.Collection);
        await raw.UpdateOneAsync(new BsonDocument("_id", initial.ValidationId), new BsonDocument("$unset", new BsonDocument
        {
            { "RecoverySchemaVersion", "" }, { "LifecycleState", "" }, { "RetryDueAtUtc", "" }
        }));
        for (var i = 0; i < 10; i++)
        {
            var unrelated = fixture.Initial() with { LifecycleState = ValidationLifecycleState.Requested, NextRetryAt = null };
            await fixture.Store.TrySaveAsync(unrelated, 0);
        }
        await fixture.Store.InitializeAsync();
        await fixture.Store.InitializeAsync();
        var document = await raw.Find(new BsonDocument("_id", initial.ValidationId)).SingleAsync();
        Assert.Equal(BsonType.DateTime, document["RetryDueAtUtc"].BsonType);
        if (abandoned) Assert.Null(await fixture.Store.TryClaimAsync(initial.ValidationId, TimeSpan.FromMinutes(1)));
        Assert.Equal(initial.ValidationId, Assert.Single(await fixture.Store.RecoverOverdueAsync(1, TimeSpan.FromMinutes(15))));
        Assert.Equal(2, (await fixture.Store.GetAsync(initial.ValidationId))!.DispatchGeneration);
        Assert.Empty(await fixture.Store.RecoverOverdueAsync(1, TimeSpan.FromMinutes(15)));
    }

    private static bool Configured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO"));
    private static ValidationLifecycle Final(ValidationLifecycle value) => value with
    {
        Version = value.Version + 1, ExecutionLease = null, ResultState = ValidationResultState.Final,
        LifecycleState = ValidationLifecycleState.Final, CurrentResult = value.CurrentResult with { Status = EmailValidationStatus.Valid }
    };
    private static EmailRevalidationMessageV1 Message(ValidationLifecycle value) => new(value.ValidationId, 2, 2,
        value.FirstValidatedAt, value.LastValidatedAt, value.NextRetryAt!.Value, "GenericSmtp",
        EmailValidationStatus.Unknown, DetailedStatus.Unknown, "1", 2, value.DispatchGeneration);

    private sealed class Fixture : IAsyncDisposable
    {
        public string Collection { get; } = $"Ev04Lifecycle_{Guid.NewGuid():N}";
        public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);
        public IMongoDatabase Database { get; }
        public MongoValidationLifecycleStore Store { get; }
        private readonly IOptions<EmailValidationOptions> _options;
        public Fixture()
        {
            var settings = new EmailValidationOptions();
            settings.Persistence.DatabaseName = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO_DATABASE") ?? "email-validation-integration-tests";
            settings.Persistence.LifecycleCollection = Collection;
            _options = Options.Create(settings);
            Database = new MongoClient(Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO")).GetDatabase(settings.Persistence.DatabaseName);
            Store = NewStore();
        }
        public MongoValidationLifecycleStore NewStore() => new(new MongoClient(Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO")),
            _options, Clock, NullLogger<MongoValidationLifecycleStore>.Instance);
        public ValidationLifecycle Initial()
        {
            var id = Guid.NewGuid().ToString("N");
            var email = $"{id}@example.test";
            var now = Clock.GetUtcNow();
            return new()
            {
                ValidationId = id, NormalizedEmail = email, MailboxKey = MailboxIdentity.Create(email).Key, Request = new(true),
                ResultState = ValidationResultState.Provisional, LifecycleState = ValidationLifecycleState.RetryWaiting,
                AttemptNumber = 1, MaximumAttempts = 2, Version = 1, DispatchGeneration = 1, RetryScheduled = true,
                FirstValidatedAt = now.AddHours(-2), LastValidatedAt = now.AddHours(-2), NextRetryAt = now,
                CurrentResult = new() { Email = email, NormalizedEmail = email, MailboxKey = MailboxIdentity.Create(email).Key,
                    Status = EmailValidationStatus.Unknown, Checks = new(), Metadata = new(new("1", "1", "1", "1"), now.AddHours(-2)) }
            };
        }
        public async ValueTask DisposeAsync() => await Database.DropCollectionAsync(Collection);
    }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
