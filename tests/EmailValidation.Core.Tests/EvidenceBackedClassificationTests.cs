using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class EvidenceBackedClassificationTests
{
    [Fact]
    public async Task OutcomeIngestion_IsIdempotent_PreservesConflicts_AndRejectsInvalidTime()
    {
        using var store = Store();
        var metrics = new RecordingMetrics();
        var service = new EmailDeliveryOutcomeIngestionService(store, metrics);
        var observation = Outcome("event-1", EmailDeliveryOutcome.Delivered);

        Assert.Equal(AppendObservationResult.Inserted,
            (await service.IngestAsync(observation)).Status);
        Assert.Equal(AppendObservationResult.Duplicate,
            (await service.IngestAsync(observation)).Status);
        Assert.Equal(AppendObservationResult.Conflict,
            (await service.IngestAsync(Outcome("event-2", EmailDeliveryOutcome.HardBounce))).Status);
        Assert.Equal(AppendObservationResult.Conflict,
            (await service.IngestAsync(observation with
            {
                OutcomeEventId = "bad-time",
                ObservedAtUtc = observation.SendAttemptAtUtc.AddMinutes(-1)
            })).Status);

        var recorded = await store.QueryAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(2, recorded.Count);
        Assert.Equal(4, metrics.OutcomeResults.Count);
    }

    [Fact]
    public async Task FeatureSnapshot_IsPredictionTimeOnly_Immutable_AndContainsNoRawEmail()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var factory = new EmailValidationFeatureSnapshotFactory(new FakeCorrelationService(), time);
        var result = Result("person@example.test", new DateTimeOffset(2026, 1, 2, 11, 59, 0, TimeSpan.Zero));

        var snapshot = await factory.CreateAsync(result, new EmailValidationRequest(
            ValidationId: "validation-1", TenantId: "tenant-1"));
        Assert.NotNull(snapshot);
        Assert.Equal(EvidenceBackedClassificationVersions.FeatureSchemaV1, snapshot.FeatureSchemaVersion);
        Assert.Equal(time.GetUtcNow(), snapshot.SnapshotAtUtc);
        Assert.Equal(result.Confidence, snapshot.HeuristicEvidenceStrength);

        var changed = result with
        {
            Confidence = 0.01,
            DomainIntelligence = result.DomainIntelligence! with { CatchAll = result.DomainIntelligence.CatchAll with { Confidence = 0.01 } }
        };
        Assert.NotEqual(changed.Confidence, snapshot.HeuristicEvidenceStrength);
        Assert.Equal(0.9, snapshot.Domain.CatchAllEvidenceStrength);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("person@example.test", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("person", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatasetBuilder_UsesOnlyPostSnapshotOutcomes_ExcludesConflicts_AndIsReproducible()
    {
        using var store = Store();
        var metrics = new RecordingMetrics();
        var snapshotAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshot1 = Snapshot("s1", "email-1", "domain-1", snapshotAt, MailProvider.GoogleWorkspace);
        var snapshot2 = Snapshot("s2", "email-2", "domain-2", snapshotAt.AddDays(1), MailProvider.Microsoft365);
        var snapshot3 = Snapshot("s3", "email-3", "domain-3", snapshotAt.AddDays(2), MailProvider.Yahoo);
        await ((IEmailValidationFeatureSnapshotStore)store).AppendAsync(snapshot1);
        await ((IEmailValidationFeatureSnapshotStore)store).AppendAsync(snapshot2);
        await ((IEmailValidationFeatureSnapshotStore)store).AppendAsync(snapshot3);

        // This authoritative event is before the prediction and must never become its label.
        await store.AppendAsync(Outcome("future-leak-guard", EmailDeliveryOutcome.Delivered) with
        {
            EmailCorrelationId = "email-1",
            SendAttemptAtUtc = snapshotAt.AddDays(-2),
            ObservedAtUtc = snapshotAt.AddDays(-1)
        });
        await store.AppendAsync(Outcome("label-1", EmailDeliveryOutcome.Delivered) with
        {
            EmailCorrelationId = "email-1",
            SendAttemptAtUtc = snapshotAt.AddDays(1),
            ObservedAtUtc = snapshotAt.AddDays(2)
        });
        await store.AppendAsync(Outcome("label-2a", EmailDeliveryOutcome.Delivered) with
        {
            EmailCorrelationId = "email-2",
            SendAttemptAtUtc = snapshotAt.AddDays(3),
            ObservedAtUtc = snapshotAt.AddDays(4)
        });
        await store.AppendAsync(Outcome("label-2b", EmailDeliveryOutcome.HardBounce) with
        {
            EmailCorrelationId = "email-2",
            SendAttemptAtUtc = snapshotAt.AddDays(3),
            ObservedAtUtc = snapshotAt.AddDays(4)
        });
        var request = new TrainingDatasetRequest(
            PredictionTargetKind.TechnicalDeliveryWithinWindow,
            "delivery-7d-v1",
            EvidenceBackedClassificationVersions.FeatureSchemaV1,
            snapshotAt.AddDays(-1),
            snapshotAt.AddDays(4),
            snapshotAt.AddDays(20));
        var builder = new TrainingDatasetBuilder(store, store, new OutcomeDefinitionCatalog(), metrics,
            new FixedTimeProvider(snapshotAt.AddDays(21)));

        var first = await builder.BuildAsync(request);
        var second = await builder.BuildAsync(request);

        Assert.Single(first.Rows);
        Assert.Equal("s1", first.Rows[0].SnapshotId);
        Assert.Equal("label-1", first.Rows[0].OutcomeEventId);
        Assert.Equal(1, first.Manifest.PositiveCount);
        Assert.Equal(1, first.Manifest.ExcludedCount);
        Assert.Equal(1, first.Manifest.UnresolvedCount);
        Assert.Equal(first.Manifest.DatasetHash, second.Manifest.DatasetHash);
        Assert.Equal(first.Manifest.DatasetId, second.Manifest.DatasetId);
    }

    [Fact]
    public void DatasetSplitter_SeparatesMailboxes_TimeWindows_AndUnseenDomains()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, 10).Select(index => Row(
            Snapshot($"s{index}", $"email-{index}", $"domain-{index % 5}", start.AddDays(index), MailProvider.GenericSmtp)))
            .Append(Row(Snapshot("duplicate-mailbox", "email-0", "domain-0", start.AddDays(9), MailProvider.GenericSmtp)))
            .ToArray();

        var splits = LeakageSafeDatasetSplitter.Split(rows, start.AddDays(4), start.AddDays(7));
        var allTemporal = splits.Training.Concat(splits.Calibration).Concat(splits.OutOfTimeTest).ToArray();
        Assert.Equal(allTemporal.Length, allTemporal.Select(item => item.EmailCorrelationId).Distinct().Count());
        Assert.All(splits.Training, item => Assert.True(item.SnapshotAtUtc < start.AddDays(4)));
        Assert.All(splits.OutOfTimeTest, item => Assert.True(item.SnapshotAtUtc >= start.AddDays(7)));
        Assert.Empty(splits.UnseenDomainTest.Select(item => item.DomainCorrelationId)
            .Intersect(allTemporal.Select(item => item.DomainCorrelationId), StringComparer.Ordinal));
    }

    [Fact]
    public void DataSufficiencyGate_RefusesEmptyRepositoryEvidence()
    {
        var dataset = new TrainingDataset(new TrainingDatasetManifest(
            "empty", DateTimeOffset.UtcNow, EvidenceBackedClassificationVersions.FeatureSchemaV1,
            "delivery-7d-v1", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, 0, 0, 0, 0, 20, 0,
            new Dictionary<MailProvider, int>(), "hash", "none", "v1"), []);
        var assessment = DataSufficiencyEvaluator.Evaluate(dataset,
            new DataSufficiencyPolicy(1000, 100, 100, TimeSpan.FromDays(30), 3, 100, 0.5, 0.9),
            20, 0, 0);
        Assert.False(assessment.ReadyToModel);
        Assert.Contains("minimum matured rows", assessment.FailedGates);
        Assert.Contains("minimum positive rows", assessment.FailedGates);
        Assert.Contains("minimum negative rows", assessment.FailedGates);
    }

    [Fact]
    public async Task ShadowOrchestrator_RecordsRecommendation_WithoutChangingHeuristicSemantics()
    {
        var options = Options.Create(new EmailValidationOptions
        {
            ClassificationModel = new ClassificationModelOptions
            {
                Mode = ModelRolloutMode.Shadow,
                MaximumMissingFeatureFraction = 1,
                MinimumVerificationReliability = 0,
                AbstentionLowerBound = 0.4,
                AbstentionUpperBound = 0.6
            }
        });
        var metrics = new RecordingMetrics();
        var snapshot = Snapshot("shadow", "email", "domain", DateTimeOffset.UtcNow, MailProvider.GoogleWorkspace);
        var heuristic = Result("person@example.test", DateTimeOffset.UtcNow) with
        {
            Status = EmailValidationStatus.Unknown
        };
        var orchestrator = new ClassificationPredictionOrchestrator(
            new FixedScorer(2), new SigmoidCalibrator(),
            new TransparentPredictionUncertaintyPolicy(options),
            new VersionedValidationDecisionPolicy(options), metrics, options,
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        var prediction = await orchestrator.ScoreAsync(snapshot, heuristic);

        Assert.NotNull(prediction);
        Assert.Equal(ModelRolloutMode.Shadow, prediction.Model?.RolloutMode);
        Assert.Equal(EmailValidationStatus.LikelyValid, prediction.Decision.Status);
        Assert.Equal(EmailValidationStatus.Unknown, heuristic.Status);
        Assert.Equal(snapshot.HeuristicEvidenceStrength, prediction.HeuristicEvidenceStrength);
    }

    [Fact]
    public void DecisionPolicy_ProtectsDeterministicInvalid_AndAbstainsToUnknown()
    {
        var options = Options.Create(new EmailValidationOptions());
        var policy = new VersionedValidationDecisionPolicy(options);
        var invalid = Result("person@example.test", DateTimeOffset.UtcNow) with
        {
            Status = EmailValidationStatus.Invalid
        };
        var model = new PredictionModelMetadata("baseline", "1", EvidenceBackedClassificationVersions.FeatureSchemaV1,
            "platt-1", "mailbox-existence-v1", "policy-1", DateTimeOffset.UtcNow, "dataset", "checksum",
            DateTimeOffset.UtcNow, ModelRolloutMode.Enforced);
        var prediction = new CalibratedPrediction(PredictionTargetKind.MailboxExistence, 0.99, model);

        Assert.True(policy.Decide(invalid, prediction,
            new PredictionUncertainty(PredictionDisposition.AcceptedPrediction, "supported")).DeterministicOverride);
        Assert.Equal(EmailValidationStatus.Unknown, policy.Decide(
            invalid with { Status = EmailValidationStatus.Risky }, prediction,
            new PredictionUncertainty(PredictionDisposition.Abstain, "near threshold")).Status);
    }

    [Fact]
    public void ProbabilityEvaluation_ReportsProperScoresCoverageAndProviderSegments()
    {
        ScoredEvaluationRow[] rows =
        [
            new(0.9, BinaryOutcomeLabel.Positive, MailProvider.GoogleWorkspace, "known"),
            new(0.1, BinaryOutcomeLabel.Negative, MailProvider.GoogleWorkspace, "known"),
            new(0.8, BinaryOutcomeLabel.Negative, MailProvider.Microsoft365, "unseen"),
            new(0.5, BinaryOutcomeLabel.Positive, MailProvider.Microsoft365, "unseen", Abstained: true)
        ];
        var report = ProbabilityModelEvaluator.Evaluate(rows, new HashSet<string> { "known" });
        Assert.Equal(4, report.Overall.Count);
        Assert.True(report.Overall.BrierScore > 0);
        Assert.True(report.Overall.LogLoss > 0);
        Assert.Equal(0.75, report.Overall.Coverage);
        Assert.Equal(0.25, report.Overall.AbstentionRate);
        Assert.Equal(2, report.ProviderSegments.Count);
        Assert.Equal(2, report.UnseenDomain.Count);
        Assert.Equal(10, report.ProbabilityBands.Count);
    }

    [Fact]
    public void InternalEvidenceStrengthAndPrediction_DoNotChangePublicJsonContract()
    {
        var result = Result("person@example.test", DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("heuristicEvidenceStrength", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prediction", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModelFailure_ReturnsNoPrediction_AndLeavesHeuristicAvailable()
    {
        var options = Options.Create(new EmailValidationOptions
        {
            ClassificationModel = new ClassificationModelOptions { Mode = ModelRolloutMode.Shadow }
        });
        var metrics = new RecordingMetrics();
        var orchestrator = new ClassificationPredictionOrchestrator(
            new ThrowingScorer(), new SigmoidCalibrator(),
            new TransparentPredictionUncertaintyPolicy(options),
            new VersionedValidationDecisionPolicy(options), metrics, options, TimeProvider.System);
        var heuristic = Result("person@example.test", DateTimeOffset.UtcNow);

        var prediction = await orchestrator.ScoreAsync(
            Snapshot("failure", "email", "domain", DateTimeOffset.UtcNow, MailProvider.GoogleWorkspace), heuristic);

        Assert.Null(prediction);
        Assert.Equal(EmailValidationStatus.Valid, heuristic.Status);
        Assert.Contains(metrics.ModelResults, item => !item);
    }

    [Fact]
    public void MongoEvidenceDocuments_RoundTripWithoutRawEmail_AndIgnoreFutureFields()
    {
        var snapshot = Snapshot("mongo", "hmac-email", "hmac-domain", DateTimeOffset.UtcNow, MailProvider.GoogleWorkspace);
        var snapshotDocument = MongoClassificationEvidenceStore.SnapshotDocument.FromModel(snapshot);
        var outcome = Outcome("mongo-outcome", EmailDeliveryOutcome.Delivered);
        var outcomeDocument = MongoClassificationEvidenceStore.OutcomeDocument.FromModel(outcome);

        Assert.Equal(snapshot, snapshotDocument.ToModel());
        Assert.Equal(outcome, outcomeDocument.ToModel());
        Assert.DoesNotContain("@", snapshotDocument.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("@", outcomeDocument.PayloadJson, StringComparison.Ordinal);
        var bson = new MongoDB.Bson.BsonDocument
        {
            ["_id"] = "future",
            ["EmailCorrelationId"] = "email",
            ["OutcomeSource"] = "source",
            ["SendAttemptAtUtc"] = DateTime.UtcNow,
            ["ObservedAtUtc"] = DateTime.UtcNow,
            ["Outcome"] = "Delivered",
            ["PayloadJson"] = JsonSerializer.Serialize(outcome),
            ["FutureOptionalField"] = true
        };
        var restored = MongoDB.Bson.Serialization.BsonSerializer
            .Deserialize<MongoClassificationEvidenceStore.OutcomeDocument>(bson);
        Assert.Equal("future", restored.Id);
    }

    [Fact]
    public void LogisticArtifact_ChecksumSchemaAndConcurrentScoring_AreDeterministic()
    {
        var path = Path.GetTempFileName();
        try
        {
            var artifact = new LogisticRegressionArtifact
            {
                ModelName = "logistic-baseline",
                ModelVersion = "1.0.0",
                Target = PredictionTargetKind.MailboxExistence,
                FeatureSchemaVersion = EvidenceBackedClassificationVersions.FeatureSchemaV1,
                CalibrationVersion = "platt-1",
                OutcomeDefinitionVersion = "mailbox-existence-v1",
                TrainingDataCutoffUtc = DateTimeOffset.UtcNow.AddDays(-1),
                TrainingDatasetId = "dataset-1",
                Intercept = -0.5,
                Coefficients = new Dictionary<string, double> { ["heuristic_evidence_strength"] = 2 },
                CalibrationSlope = 1.1,
                CalibrationIntercept = -0.1,
                L2Regularization = 0.01,
                RandomSeed = 17
            };
            File.WriteAllText(path, JsonSerializer.Serialize(artifact));
            var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
                .ToLowerInvariant();
            var options = Options.Create(new EmailValidationOptions
            {
                ClassificationModel = new ClassificationModelOptions
                {
                    Mode = ModelRolloutMode.Shadow,
                    ArtifactPath = path,
                    ArtifactChecksum = checksum
                }
            });
            var scorer = new LogisticRegressionProbabilityScorer(new LogisticRegressionArtifactProvider(options));
            var snapshot = Snapshot("model", "email", "domain", DateTimeOffset.UtcNow, MailProvider.GoogleWorkspace);
            var scores = new double[32];
            Parallel.For(0, scores.Length, index => scores[index] = scorer.Score(snapshot).RawScore);
            Assert.All(scores, score => Assert.Equal(scores[0], score));

            var badOptions = Options.Create(new EmailValidationOptions
            {
                ClassificationModel = new ClassificationModelOptions
                {
                    Mode = ModelRolloutMode.Shadow,
                    ArtifactPath = path,
                    ArtifactChecksum = new string('0', 64)
                }
            });
            Assert.Throws<InvalidDataException>(() =>
                new LogisticRegressionProbabilityScorer(new LogisticRegressionArtifactProvider(badOptions)).Score(snapshot));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LocalClassificationEvidenceStore Store() => new(Options.Create(new EmailValidationOptions
    {
        Persistence = new PersistenceOptions { Enabled = false, Provider = "Json" }
    }));

    private static EmailDeliveryOutcomeObservation Outcome(string eventId, EmailDeliveryOutcome outcome)
    {
        var send = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        return new()
        {
            OutcomeEventId = eventId,
            EmailCorrelationId = "email-correlation",
            Outcome = outcome,
            Confidence = OutcomeConfidence.Authoritative,
            OutcomeSource = "provider-webhook",
            SourceEventId = eventId,
            Provider = MailProvider.GoogleWorkspace,
            SendAttemptAtUtc = send,
            ObservedAtUtc = send.AddHours(1),
            NormalizationVersion = "delivery-outcome-normalization-v1"
        };
    }

    private static EmailValidationResult Result(string email, DateTimeOffset validatedAt)
    {
        var domain = new DomainIntelligence
        {
            Domain = "example.test",
            DomainExists = true,
            Dns = new DnsLookupResult(DnsStatus.Success, true, [new MxRecord(10, "mx.example.test")], false, TimeSpan.Zero),
            Provider = new ProviderDetectionResult(MailProvider.GoogleWorkspace, 0.85, TopologyFingerprint: "mx-fingerprint"),
            CatchAll = new CatchAllDetectionResult(CatchAllStatus.NotCatchAll, 2, 0, 2, 0, Confidence: 0.9),
            ObservedAt = validatedAt
        };
        return new EmailValidationResult
        {
            Email = email,
            NormalizedEmail = email,
            Status = EmailValidationStatus.Valid,
            Confidence = 0.92,
            Checks = new EmailValidationChecks
            {
                SyntaxValid = true, DomainExists = true, MxPresent = true,
                CatchAll = CatchAllStatus.NotCatchAll, Mailbox = SmtpMailboxStatus.Accepted
            },
            MailProvider = MailProvider.GoogleWorkspace,
            Provider = domain.Provider,
            MxRecords = domain.MxRecords,
            DomainIntelligence = domain,
            CatchAll = new CatchAllValidationDetails(CatchAllStatus.NotCatchAll, 0.9),
            EvidenceQuality = EvidenceQuality.Conclusive,
            Metadata = new ValidationResultMetadata(
                new ValidationPolicyVersions("engine", "classification", "heuristic", "provider"), validatedAt,
                MxTopologyFingerprint: "mx-fingerprint")
        };
    }

    private static EmailValidationFeatureSnapshot Snapshot(
        string id, string emailKey, string domainKey, DateTimeOffset at, MailProvider provider) => new()
        {
            SnapshotId = id,
            ValidationId = $"validation-{id}",
            EmailCorrelationId = emailKey,
            DomainCorrelationId = domainKey,
            SnapshotAtUtc = at,
            FeatureSchemaVersion = EvidenceBackedClassificationVersions.FeatureSchemaV1,
            Syntax = new(true, true, false, false, false, false),
            Domain = new(true, DnsStatus.Success, true, false, 1, false, provider, 0.8,
                DnsSecurityState.Unknown, AuthenticationRecordState.Unknown, AuthenticationRecordState.Unknown,
                CatchAllStatus.Unknown, 0.2, "mx"),
            Smtp = new(SmtpProbeDisposition.NotAttempted, null, null, null,
                SmtpResponseCategory.NotAttempted, null, false, false, false, false, false, false),
            History = new(0, 0, 0, 0, VerificationReliabilityLevel.Unknown, 0, 0, 0, 0, 0),
            Operational = new(ValidationResultSource.LiveValidation, 1, EvidenceQuality.Partial, false, null, null),
            HeuristicEvidenceStrength = 0.7,
            HeuristicStatus = EmailValidationStatus.Unknown
        };

    private static TrainingDatasetRow Row(EmailValidationFeatureSnapshot snapshot) => new(
        snapshot.SnapshotId, snapshot.EmailCorrelationId, snapshot.DomainCorrelationId,
        snapshot.SnapshotAtUtc, snapshot, BinaryOutcomeLabel.Positive,
        $"outcome-{snapshot.SnapshotId}", snapshot.SnapshotAtUtc.AddDays(1), OutcomeConfidence.High);

    private sealed class FakeCorrelationService : IEmailCorrelationService
    {
        public ValueTask<EmailCorrelation?> TryCreateAsync(
            string? tenantId, string normalizedEmail, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{tenantId}|{normalizedEmail}"))).ToLowerInvariant();
            return ValueTask.FromResult<EmailCorrelation?>(new(id, "test-key-v1"));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingMetrics : IClassificationFoundationMetrics
    {
        public List<AppendObservationResult> OutcomeResults { get; } = [];
        public List<bool> ModelResults { get; } = [];
        public void RecordOutcome(AppendObservationResult result, EmailDeliveryOutcome outcome) => OutcomeResults.Add(result);
        public void RecordSnapshot(bool created) { }
        public void RecordModelScored(ModelRolloutMode mode, bool succeeded, bool abstained, bool disagreed, TimeSpan elapsed) =>
            ModelResults.Add(succeeded);
        public void RecordDataset(TrainingDatasetManifest manifest) { }
    }

    private sealed class FixedScorer(double rawScore) : IProbabilityScorer
    {
        public RawModelPrediction Score(EmailValidationFeatureSnapshot snapshot) => new(
            PredictionTargetKind.MailboxExistence,
            rawScore,
            new PredictionModelMetadata("logistic-baseline", "1", snapshot.FeatureSchemaVersion,
                "platt-1", "mailbox-existence-v1", "policy-1", DateTimeOffset.UtcNow,
                "dataset-1", "checksum", DateTimeOffset.MinValue, ModelRolloutMode.Disabled));
    }

    private sealed class SigmoidCalibrator : IProbabilityCalibrator
    {
        public CalibratedPrediction Calibrate(RawModelPrediction prediction) => new(
            prediction.Target, 1 / (1 + Math.Exp(-prediction.RawScore)), prediction.Model);
    }

    private sealed class ThrowingScorer : IProbabilityScorer
    {
        public RawModelPrediction Score(EmailValidationFeatureSnapshot snapshot) =>
            throw new InvalidOperationException("artifact unavailable");
    }
}
