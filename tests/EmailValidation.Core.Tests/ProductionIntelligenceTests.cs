using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class ProductionIntelligenceTests
{
    [Fact]
    public async Task PersistentStore_SeparatelyRestoresDomainAndMailboxAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), "email-validation-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = StoreOptions(path);
            var first = new JsonValidationIntelligenceStore(options);
            var domain = Domain(DateTimeOffset.UtcNow.AddHours(1));
            var mailbox = Mailbox(Result(), DateTimeOffset.UtcNow);
            await first.SaveDomainAsync(domain);
            await first.SaveMailboxAsync(mailbox);

            var second = new JsonValidationIntelligenceStore(options);
            var restoredDomain = await second.GetDomainAsync("example.test");
            var restoredMailbox = await second.GetMailboxAsync("person@example.test");

            Assert.NotNull(restoredDomain);
            Assert.Equal("mx.example.test", restoredDomain!.MxRecords.Single().Host);
            Assert.NotNull(restoredMailbox);
            Assert.Equal(EmailValidationStatus.LikelyValid, restoredMailbox!.PreviousStatus);
            Assert.Equal("person@example.test", restoredMailbox.NormalizedEmail);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task PersistentDomainCache_LoadsFreshAndRejectsStaleEvidence()
    {
        var store = new TestIntelligenceStore
        {
            Domain = Domain(DateTimeOffset.UtcNow.AddMinutes(5))
        };
        var cache = new PersistentDomainValidationCache(store);

        Assert.NotNull(await cache.GetAsync("example.test"));

        store.Domain = Domain(DateTimeOffset.UtcNow.AddMinutes(-1));
        var newCache = new PersistentDomainValidationCache(store);
        Assert.Null(await newCache.GetAsync("example.test"));
    }

    [Fact]
    public async Task PersistentStore_RestoresOutcomeSnapshotsAndHardBounceSuppression()
    {
        var path = Path.Combine(Path.GetTempPath(), "email-validation-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = StoreOptions(path);
            var first = new JsonValidationIntelligenceStore(options);
            var snapshot = Snapshot(
                EmailValidationStatus.Valid,
                0.98,
                new ValidationPolicyVersions("1", "2", "3", "4"),
                DateTimeOffset.UtcNow.AddHours(-1));
            await first.RecordAsync(new DeliveryOutcomeRecord(
                snapshot,
                DeliveryOutcomeKind.HardBounce,
                DateTimeOffset.UtcNow,
                "AuthorizedMta"));

            var second = new JsonValidationIntelligenceStore(options);
            var outcomes = await second.QueryAsync(new CalibrationQuery(Status: EmailValidationStatus.Valid));
            var suppression = await second.GetAsync("person@example.test");

            Assert.Single(outcomes);
            Assert.Equal(0.98, outcomes[0].Prediction.PredictedConfidence);
            Assert.Equal("HistoricalHardBounce", suppression?.Reason);
            Assert.Equal("AuthorizedMta", suppression?.Source);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void ResultReuse_RequiresFreshEvidenceAndMatchingPolicy()
    {
        var options = Options.Create(new EmailValidationOptions());
        var policy = options.Value.Policy.ToVersions();
        var now = DateTimeOffset.UtcNow;
        var reuse = new ValidationResultReusePolicy(options);
        var mailbox = Mailbox(Result(), now.AddMinutes(-5));

        Assert.True(reuse.CanReuse(mailbox, Domain(now.AddHours(1)), new EmailValidationRequest(true), policy, now));
        Assert.False(reuse.CanReuse(mailbox with { LastValidatedAt = now.AddDays(-1) }, Domain(now.AddHours(1)),
            new EmailValidationRequest(true), policy, now));
        Assert.False(reuse.CanReuse(mailbox, Domain(now.AddHours(1)), new EmailValidationRequest(true),
            policy with { ClassificationPolicyVersion = "3.0.0" }, now));
        Assert.False(reuse.CanReuse(mailbox, Domain(now.AddHours(1)) with
        {
            Provider = Domain(now.AddHours(1)).Provider with { TopologyFingerprint = "changed" }
        }, new EmailValidationRequest(true), policy, now));
    }

    [Fact]
    public async Task SingleFlight_CollapsesConcurrentRequests()
    {
        var singleFlight = new ValidationSingleFlight();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<EmailValidationResult> Execute() => singleFlight.ExecuteAsync("person@example.test", async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return Result();
        });

        var consumers = Enumerable.Range(0, 8).Select(_ => Execute()).ToArray();
        await Task.Yield();
        release.SetResult();
        var results = await Task.WhenAll(consumers);

        Assert.Equal(1, calls);
        Assert.All(results, result => Assert.Equal("person@example.test", result.NormalizedEmail));
    }

    [Fact]
    public async Task IntelligenceValidator_ReusesFreshResultWithoutSecondLiveExecution()
    {
        var options = Options.Create(new EmailValidationOptions());
        var executor = new CountingExecutor();
        var store = new TestIntelligenceStore { Domain = Domain(DateTimeOffset.UtcNow.AddHours(1)) };
        var validator = new IntelligenceEmailValidator(
            executor,
            new EmailNormalizer(),
            store,
            new ValidationSingleFlight(),
            new ValidationResultReusePolicy(options),
            new EmailRiskIntelligence([new ExistingIntelligenceRiskDataSource()]),
            new ValidationQualityMetrics(),
            options,
            TimeProvider.System);

        var first = await validator.ValidateAsync("Person@Example.Test", new EmailValidationRequest(true));
        var second = await validator.ValidateAsync("person@example.test", new EmailValidationRequest(true));

        Assert.Equal(1, executor.Calls);
        Assert.False(first.Metadata!.Reused);
        Assert.True(second.Metadata!.Reused);
        Assert.Equal(first.Status, second.Status);
    }

    [Fact]
    public async Task DeliveryOutcomes_PreserveSnapshotsAndCalculateCalibrationMetrics()
    {
        var recorder = new InMemoryDeliveryOutcomeRecorder();
        var policy = new ValidationPolicyVersions("1", "2.1", "3", "4");
        var validatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var positive = Snapshot(EmailValidationStatus.LikelyValid, 0.90, policy, validatedAt);
        var falseValid = Snapshot(EmailValidationStatus.Valid, 0.98, policy, validatedAt);
        var falseInvalid = Snapshot(EmailValidationStatus.Invalid, 0.95, policy, validatedAt);
        await recorder.RecordAsync(new(positive, DeliveryOutcomeKind.Delivered, DateTimeOffset.UtcNow));
        await recorder.RecordAsync(new(falseValid, DeliveryOutcomeKind.HardBounce, DateTimeOffset.UtcNow));
        await recorder.RecordAsync(new(falseInvalid, DeliveryOutcomeKind.Delivered, DateTimeOffset.UtcNow));

        var result = await new ConfidenceCalibrationService(recorder).EvaluateAsync(new CalibrationQuery());

        Assert.Equal(3, result.Metrics.SampleCount);
        Assert.Equal(0.6667, result.Metrics.DeliveryRate);
        Assert.Equal(0.3333, result.Metrics.HardBounceRate);
        Assert.Equal(0.5, result.Metrics.FalseValidRate);
        Assert.Equal(1, result.Metrics.FalseInvalidRate);
        Assert.False(result.IsStatisticallyCalibrated);
        Assert.Equal(validatedAt, (await recorder.QueryAsync(new CalibrationQuery(Status: EmailValidationStatus.LikelyValid)))
            .Single().Prediction.ValidatedAt);
    }

    [Fact]
    public async Task DeliveryOutcomeRecorder_DefensivelyCopiesPredictionReasonCodes()
    {
        var recorder = new InMemoryDeliveryOutcomeRecorder();
        var reasons = new[] { ReasonCode.MailboxAccepted };
        var snapshot = Snapshot(
            EmailValidationStatus.LikelyValid,
            0.9,
            new ValidationPolicyVersions("1", "2", "3", "4"),
            DateTimeOffset.UtcNow) with
        { ReasonCodes = reasons };
        await recorder.RecordAsync(new(snapshot, DeliveryOutcomeKind.Delivered, DateTimeOffset.UtcNow));

        reasons[0] = ReasonCode.MailboxRejected;

        var recorded = (await recorder.QueryAsync(new CalibrationQuery())).Single();
        Assert.Equal(ReasonCode.MailboxAccepted, recorded.Prediction.ReasonCodes.Single());
    }

    [Fact]
    public async Task RiskIntelligence_DoesNotChangeDeliverabilityForKnownSuppression()
    {
        var source = new ExistingIntelligenceRiskDataSource();
        var risk = new EmailRiskIntelligence([source]);
        var result = await risk.EvaluateAsync(new EmailRiskContext(
            "person@example.test",
            EmailValidationStatus.LikelyValid,
            0.90,
            new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true },
            Domain(DateTimeOffset.UtcNow.AddHours(1)),
            new EmailAddressIntelligence
            {
                Email = "person@example.test",
                Suppression = new SuppressionResult(SuppressionStatus.Suppressed, "HistoricalHardBounce")
            }));

        Assert.Equal(EmailValidationStatus.LikelyValid, result.DeliverabilityStatus);
        Assert.Equal(MailingRiskLevel.High, result.MailingRisk);
        Assert.Contains(MailingRiskReason.KnownSuppression, result.RiskReasons);
    }

    [Fact]
    public void Classification_DoesNotUseSuppressionToRewriteDeliverabilityOrConfidence()
    {
        var domain = Domain(DateTimeOffset.UtcNow.AddHours(1));
        var provider = new ProviderValidationResult(
            MailProvider.GenericSmtp,
            0.8,
            SmtpResponseCategory.Accepted,
            AcceptanceStrength.High,
            [],
            "Recipient accepted.",
            VerificationReliability: 0.9,
            VerificationReliabilityLevel: VerificationReliabilityLevel.High);
        EmailClassificationEvidence Evidence(EmailAddressIntelligence address) => new(
            true,
            DnsStatus.Success,
            domain,
            false,
            new MailboxEvidence(
                domain.Domain,
                "mx.example.test",
                new SmtpProbeResult(SmtpMailboxStatus.Accepted, 250, null, TimeSpan.Zero),
                provider),
            HistoricalSignalSummary.Empty)
        { AddressIntelligence = address };
        var classifier = new EmailClassificationEngine();
        var clean = classifier.Classify(Evidence(new EmailAddressIntelligence { Email = "person@example.test" }));
        var suppressed = classifier.Classify(Evidence(new EmailAddressIntelligence
        {
            Email = "person@example.test",
            Suppression = new SuppressionResult(SuppressionStatus.Suppressed, "HistoricalHardBounce")
        }));

        Assert.Equal(clean.Status, suppressed.Status);
        Assert.Equal(clean.Confidence, suppressed.Confidence);
        Assert.Contains(ReasonCode.SuppressionMatch, suppressed.ReasonCodes);
    }

    [Theory]
    [InlineData("gmal.com", "gmail.com")]
    [InlineData("hotnail.com", "hotmail.com")]
    [InlineData("yaho.com", "yahoo.com")]
    public void TypoDetection_IsConservativeForKnownProviders(string typo, string expected)
    {
        var detector = new EmailTypoDetector(Options.Create(new EmailValidationOptions()));
        var result = detector.Detect("john", typo);
        Assert.True(result.TypoDetected);
        Assert.Equal($"john@{expected}", result.SuggestedEmail);
        Assert.True(result.Confidence >= 0.9);
        Assert.False(detector.Detect("john", "smallcustomcompany.com").TypoDetected);
    }

    [Fact]
    public void SubStatusMapper_MapsSuppressionIndependently()
    {
        var result = Result() with
        {
            MailingRisk = new EmailRiskResult(
                EmailValidationStatus.LikelyValid, 0.9, MailingRiskLevel.High,
                [MailingRiskReason.KnownSuppression], [])
        };
        Assert.Equal(DetailedStatus.KnownSuppression, ValidationSubStatusMapper.Map(result));
    }

    [Fact]
    public void QualityMetrics_ExposeUnknownAndProviderRates()
    {
        var metrics = new ValidationQualityMetrics();
        metrics.Record(Result());
        metrics.Record(Result() with { Status = EmailValidationStatus.Unknown, DurationMs = 30 });

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(2, snapshot.TotalValidations);
        Assert.Equal(0.5, snapshot.StatusRates[EmailValidationStatus.Unknown]);
        Assert.Equal(0.5, snapshot.Providers.Single().UnknownRate);
    }

    [Fact]
    public void ExistingConfidence_RemainsExplicitlyHeuristic()
    {
        Assert.Equal(ConfidenceType.Heuristic, Result().ConfidenceType);
    }

    private static IOptions<EmailValidationOptions> StoreOptions(string path) => Options.Create(new EmailValidationOptions
    {
        Persistence = new PersistenceOptions { Enabled = true, StoragePath = path }
    });

    private static DomainIntelligence Domain(DateTimeOffset expiresAt) => new()
    {
        Domain = "example.test",
        DomainExists = true,
        Dns = new DnsLookupResult(DnsStatus.Success, true, [new MxRecord(10, "mx.example.test")], false, TimeSpan.Zero),
        Provider = new ProviderDetectionResult(
            MailProvider.GenericSmtp, 0.8, TopologyFingerprint: "topology-1"),
        CatchAll = new CatchAllDetectionResult(CatchAllStatus.NotCatchAll, 1, 0, 1, 0, Confidence: 0.9),
        ObservedAt = DateTimeOffset.UtcNow,
        EvidenceExpiresAt = expiresAt,
        StrategyVersion = "1.0.0"
    };

    private static EmailValidationResult Result() => new()
    {
        Email = "person@example.test",
        NormalizedEmail = "person@example.test",
        Status = EmailValidationStatus.LikelyValid,
        Confidence = 0.9,
        ConfidenceType = ConfidenceType.Heuristic,
        Checks = new EmailValidationChecks
        {
            SyntaxValid = true,
            DomainExists = true,
            MxPresent = true,
            Mailbox = SmtpMailboxStatus.Accepted,
            CatchAll = CatchAllStatus.NotCatchAll
        },
        MailProvider = MailProvider.GenericSmtp,
        Provider = new ProviderDetectionResult(
            MailProvider.GenericSmtp, 0.8, TopologyFingerprint: "topology-1"),
        ReasonCodes = [ReasonCode.MailboxAccepted],
        DetailedStatus = DetailedStatus.MailboxAccepted,
        DetailedStatuses = [DetailedStatus.MailboxAccepted],
        Metadata = new ValidationResultMetadata(
            new ValidationPolicyVersions("1.0.0", "2.1.0", "3.0.0", "1.0.0"),
            DateTimeOffset.UtcNow,
            MxTopologyFingerprint: "topology-1")
    };

    private static MailboxIntelligence Mailbox(EmailValidationResult result, DateTimeOffset validatedAt) => new()
    {
        NormalizedEmail = result.NormalizedEmail!,
        PreviousStatus = result.Status,
        PreviousMailboxResult = result.Checks.Mailbox,
        PreviousConfidence = result.Confidence,
        PreviousConfidenceType = result.ConfidenceType,
        LastValidatedAt = validatedAt,
        LastStrongPositiveEvidenceAt = validatedAt,
        ProviderAtValidation = result.MailProvider,
        Policy = result.Metadata!.Policy,
        ReasonCodes = result.ReasonCodes,
        MxTopologyFingerprint = "topology-1",
        UsedLiveSmtp = true,
        LastResult = result
    };

    private static ValidationPredictionSnapshot Snapshot(
        EmailValidationStatus status,
        double confidence,
        ValidationPolicyVersions policy,
        DateTimeOffset validatedAt) => new(
            "person@example.test", status, confidence, ConfidenceType.Heuristic,
            MailProvider.GenericSmtp, CatchAllStatus.NotCatchAll,
            VerificationReliabilityLevel.High, policy, validatedAt, []);

    private sealed class TestIntelligenceStore : IValidationIntelligenceStore
    {
        public DomainIntelligence? Domain { get; set; }
        public MailboxIntelligence? Mailbox { get; set; }
        public Task<DomainIntelligence?> GetDomainAsync(string domain, CancellationToken cancellationToken = default) =>
            Task.FromResult(Domain);
        public Task<MailboxIntelligence?> GetMailboxAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(Mailbox);
        public Task SaveDomainAsync(DomainIntelligence intelligence, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SaveMailboxAsync(MailboxIntelligence intelligence, CancellationToken cancellationToken = default)
        {
            Mailbox = intelligence;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingExecutor : IEmailValidationExecutor
    {
        public int Calls { get; private set; }

        public Task<EmailValidationResult> ValidateAsync(
            string email,
            EmailValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result() with { Email = email });
        }
    }
}
