using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class MailboxIdentityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SyntheticValidation_PreservesRecipientCaseAcrossLiveCacheAndDurableRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailbox-pipeline-{Guid.NewGuid():N}");
        var settings = new EmailValidationOptions();
        settings.Smtp.Enabled = true;
        settings.CatchAll.Enabled = false;
        settings.Persistence.Enabled = true;
        settings.Persistence.StoragePath = root;
        var options = Options.Create(settings);
        var smtp = new RecordingSmtp();
        var dns = new SyntheticDns();
        IntelligenceEmailValidator Validator()
        {
            var store = new JsonValidationIntelligenceStore(options);
            var executor = EmailValidatorTests.CreateValidator(dns, settings, smtp: smtp,
                cache: new PersistentDomainValidationCache(store, options));
            return new(executor, new EmailNormalizer(), store, new InMemoryValidationResultCache(options, TimeProvider.System),
                new ValidationSingleFlight(), new ValidationResultReusePolicy(options),
                new EmailRiskIntelligence([new ExistingIntelligenceRiskDataSource()]),
                new ValidationQualityMetrics(), new ValidationPersistenceMetrics(), options, TimeProvider.System,
                NullLogger<IntelligenceEmailValidator>.Instance, new ConfidenceLevelPolicy());
        }
        try
        {
            var validator = Validator();
            var upper = await validator.ValidateAsync("User@BÜCHER.example", new(true));
            var lower = await validator.ValidateAsync("user@xn--bcher-kva.example", new(true));
            var cached = await validator.ValidateAsync("User@XN--BCHER-KVA.example", new(true));
            Assert.Collection(smtp.Recipients,
                recipient => Assert.Equal("User@xn--bcher-kva.example", recipient),
                recipient => Assert.Equal("user@xn--bcher-kva.example", recipient));
            Assert.NotEqual(upper.MailboxKey, lower.MailboxKey);
            Assert.Equal(upper.MailboxKey, cached.MailboxKey);
            Assert.Equal(ValidationResultSource.MemoryCache, cached.Metadata!.ResultSource);
            var restarted = Validator();
            var restoredUpper = await restarted.ValidateAsync("User@bücher.example", new(true));
            var restoredLower = await restarted.ValidateAsync("user@bücher.example", new(true));
            Assert.Equal(upper.MailboxKey, restoredUpper.MailboxKey);
            Assert.Equal(lower.MailboxKey, restoredLower.MailboxKey);
            Assert.Equal(ValidationResultSource.PersistentReuse, restoredUpper.Metadata!.ResultSource);
            Assert.Equal(ValidationResultSource.PersistentReuse, restoredLower.Metadata!.ResultSource);
            Assert.Equal(2, smtp.Recipients.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SuppressionMigration_RequiresCurrentIdentityAndSeparatesCaseVariants()
    {
        var root = Path.Combine(Path.GetTempPath(), $"suppression-migration-{Guid.NewGuid():N}");
        var options = Options.Create(new EmailValidationOptions
        {
            Persistence = new PersistenceOptions { Enabled = true, StoragePath = root }
        });
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "suppressions"));
            var legacy = new SuppressionEntry("user@example.test", "HistoricalHardBounce", "old", DateTimeOffset.UtcNow);
            var path = Path.Combine(root, "suppressions", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacy.NormalizedEmail))) + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(legacy, JsonOptions));
            var store = new JsonValidationIntelligenceStore(options);
            var snapshot = ValidationPredictionSnapshots.FromResult(Result("user@example.test")) with { MailboxKey = null };
            await store.RecordAsync(new DeliveryOutcomeRecord(snapshot, DeliveryOutcomeKind.HardBounce, DateTimeOffset.UtcNow, "old"));
            Assert.Null(await store.GetAsync("user@example.test"));
            Assert.Null(await store.GetAsync("User@example.test"));
            await store.RecordAsync(new DeliveryOutcomeRecord(ValidationPredictionSnapshots.FromResult(Result("User@example.test")),
                DeliveryOutcomeKind.HardBounce, DateTimeOffset.UtcNow, "new"));
            var restarted = new JsonValidationIntelligenceStore(options);
            Assert.Null(await restarted.GetAsync("user@example.test"));
            Assert.Equal("new", (await restarted.GetAsync("User@EXAMPLE.test"))!.Source);
            Assert.True(File.Exists(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class SyntheticDns : IDnsMailResolver
    {
        public Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnsLookupResult(DnsStatus.Success, true, [new MxRecord(10, "mx.example.test")], false, TimeSpan.Zero));
    }

    private sealed class RecordingSmtp : ISmtpMailboxProbe
    {
        public List<string> Recipients { get; } = [];
        public Task<SmtpProbeResult> ProbeAsync(string mxHost, string recipient, CancellationToken cancellationToken = default)
        {
            Recipients.Add(recipient);
            return Task.FromResult(new SmtpProbeResult(SmtpMailboxStatus.Accepted, 250, "250 accepted", TimeSpan.Zero,
                Evidence: new SmtpEvidence(SmtpCommand.RcptTo, 250, "2.1.5", SmtpResponseCategory.Accepted,
                    SmtpResponseTextClassification.Success, 1, MailProvider.GenericSmtp, mxHost, 1, DateTimeOffset.UtcNow)));
        }
    }

    [Fact]
    public void JobIdempotency_PreservesLocalCaseAndNormalizesDomain()
    {
        Assert.Equal(IdempotencyRequestHasher.HashJobRequest(["User@EXAMPLE.test"], true),
            IdempotencyRequestHasher.HashJobRequest(["User@example.test"], true));
        Assert.NotEqual(IdempotencyRequestHasher.HashJobRequest(["User@example.test"], true),
            IdempotencyRequestHasher.HashJobRequest(["user@example.test"], true));
    }
    [Theory]
    [InlineData("User@EXAMPLE.test", "User@example.test")]
    [InlineData("User@BÜCHER.example", "User@xn--bcher-kva.example")]
    [InlineData(" User@Example.test. ", "User@example.test")]
    public void DomainNormalization_HasOneVersionedIdentity(string input, string canonical)
    {
        Assert.Equal(MailboxIdentity.Create(canonical), MailboxIdentity.Create(input));
        Assert.StartsWith(MailboxIdentity.KeyPrefix, MailboxIdentity.Create(input).Key, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("User@example.test", "user@example.test")]
    [InlineData("Üser@example.test", "üser@example.test")]
    [InlineData("User@gmail.com", "user@gmail.com")]
    [InlineData("first.last@gmail.com", "firstlast@gmail.com")]
    [InlineData("user+tag@gmail.com", "user@gmail.com")]
    public void ExactLocalPolicy_DoesNotInferProviderEquivalence(string left, string right)
    {
        Assert.NotEqual(MailboxIdentity.Create(left).Key, MailboxIdentity.Create(right).Key);
        Assert.False(MailboxIdentity.Matches(MailboxIdentity.Create(left).Key, right));
    }

    [Fact]
    public async Task SingleFlight_DoesNotJoinCaseDistinctMailboxes()
    {
        var flight = new ValidationSingleFlight();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<EmailValidationResult> Observe(CancellationToken cancellation)
        {
            if (Interlocked.Increment(ref calls) == 2) started.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return Result("User@example.test");
        }
        var first = flight.ExecuteAsync(MailboxIdentity.Create("User@example.test").Key, Observe);
        var second = flight.ExecuteAsync(MailboxIdentity.Create("user@example.test").Key, Observe);
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task JsonRestart_IsolatesLegacyEvidenceAndBothNewCaseVariants()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailbox-migration-{Guid.NewGuid():N}");
        var options = Options.Create(new EmailValidationOptions
        {
            Persistence = new PersistenceOptions { Enabled = true, StoragePath = root }
        });
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "mailboxes"));
            var old = Mailbox("user@example.test") with { MailboxKey = null };
            var path = Path.Combine(root, "mailboxes", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(old.NormalizedEmail))) + ".json");
            var original = JsonSerializer.Serialize(old, JsonOptions);
            await File.WriteAllTextAsync(path, original);
            var store = new JsonValidationIntelligenceStore(options);
            Assert.Null(await store.GetMailboxAsync("user@example.test"));
            Assert.Null(await store.GetMailboxAsync("User@example.test"));
            await store.SaveMailboxAsync(Mailbox("User@example.test"));
            await store.SaveMailboxAsync(Mailbox("user@example.test"));
            var restarted = new JsonValidationIntelligenceStore(options);
            Assert.Equal("User@example.test", (await restarted.GetMailboxAsync("User@EXAMPLE.test"))!.NormalizedEmail);
            Assert.Equal("user@example.test", (await restarted.GetMailboxAsync("user@example.test"))!.NormalizedEmail);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Equal(3, Directory.GetFiles(Path.Combine(root, "mailboxes")).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MongoMapping_VersionsKeysAndRejectsLegacyAndMismatchedPayloads()
    {
        var upper = MongoValidationIntelligenceStore.MailboxIntelligenceDocument.FromModel(Mailbox("User@example.test"));
        var lower = MongoValidationIntelligenceStore.MailboxIntelligenceDocument.FromModel(Mailbox("user@example.test"));
        Assert.NotEqual(upper.Id, lower.Id);
        Assert.Equal("User@example.test", upper.ToModel()!.NormalizedEmail);
        upper.MailboxKey = null;
        Assert.Null(upper.ToModel());
        lower.NormalizedEmail = "User@example.test";
        Assert.Null(lower.ToModel());
        Assert.Throws<ArgumentException>(() => MongoValidationIntelligenceStore.MailboxIntelligenceDocument.FromModel(
            Mailbox("user@example.test") with { MailboxKey = null }));
    }

    [Fact]
    public async Task Correlation_IsCaseSensitiveDomainCanonicalAndVersioned()
    {
        var settings = new EmailValidationOptions();
        settings.Projection.Privacy.EmailHashKey = new string('k', 32);
        var service = new HmacEmailCorrelationService(Options.Create(settings), NullLogger<HmacEmailCorrelationService>.Instance);
        var upper = await service.TryCreateAsync("tenant", "User@EXAMPLE.test");
        var same = await service.TryCreateAsync("tenant", "User@example.test");
        var lower = await service.TryCreateAsync("tenant", "user@example.test");
        Assert.Equal(upper, same);
        Assert.NotEqual(upper, lower);
        Assert.Contains(MailboxIdentity.KeyVersion, JsonSerializer.Serialize(upper), StringComparison.Ordinal);
        var domain = await service.TryCreateAsync("tenant", "domain:BÜCHER.example");
        Assert.NotNull(domain);
        Assert.Equal(domain, await service.TryCreateAsync("tenant", "domain:xn--bcher-kva.example"));
        var snapshot = await new EmailValidationFeatureSnapshotFactory(service, TimeProvider.System)
            .CreateAsync(Result("User@example.test"), new(ValidationId: "new-validation", TenantId: "tenant"));
        Assert.NotNull(snapshot);
    }

    private static EmailValidationResult Result(string email) => new()
    {
        Email = email, NormalizedEmail = MailboxIdentity.Create(email).Address,
        MailboxKey = MailboxIdentity.Create(email).Key,
        Status = EmailValidationStatus.Valid,
        Checks = new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true, Mailbox = SmtpMailboxStatus.Accepted },
        Metadata = new(new("1", "1", "1", "1"), DateTimeOffset.UtcNow)
    };

    private static MailboxIntelligence Mailbox(string email)
    {
        var result = Result(email);
        return new()
        {
            NormalizedEmail = result.NormalizedEmail!, MailboxKey = result.MailboxKey,
            PreviousStatus = result.Status, PreviousMailboxResult = result.Checks.Mailbox,
            PreviousConfidence = .95, PreviousConfidenceType = ConfidenceType.Heuristic,
            LastValidatedAt = result.Metadata!.ValidatedAt, Policy = result.Metadata.Policy,
            ProviderAtValidation = MailProvider.GenericSmtp, UsedLiveSmtp = true, LastResult = result
        };
    }
}
