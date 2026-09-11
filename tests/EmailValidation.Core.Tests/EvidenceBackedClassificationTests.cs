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
        Assert.Equal(EvidenceBackedClassificationVersions.FeatureSchemaV2, snapshot.FeatureSchemaVersion);
        Assert.Equal(time.GetUtcNow(), snapshot.SnapshotAtUtc);
        Assert.Equal(result.Confidence, snapshot.HeuristicEvidenceStrength);
        Assert.Equal(DomainRecipientBehavior.RecipientSpecific, snapshot.Domain.RecipientBehavior);
        Assert.False(snapshot.Domain.AcceptAllCandidate);
        Assert.False(snapshot.Domain.MxEvidenceConflicting);
        Assert.False(snapshot.Domain.ProviderEvidenceConflicting);

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
    public async Task FeatureSnapshotV2_CarriesRecipientBehaviorAndConflictSemantics()
    {
        var at = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var factory = new EmailValidationFeatureSnapshotFactory(
            new FakeCorrelationService(), new FixedTimeProvider(at));
        var baseline = Result("person@example.test", at.AddMinutes(-1));
        var candidate = new CatchAllDetectionResult(
            CatchAllStatus.Unknown, 2, 2, 0, 0, "Candidate", 0.75)
        {
            ReasonCode = CatchAllReasonCode.AcceptAllCandidate
        };
        var result = baseline with
        {
            Status = EmailValidationStatus.Unknown,
            Checks = baseline.Checks with
            {
                CatchAll = CatchAllStatus.Unknown,
                Mailbox = SmtpMailboxStatus.Accepted
            },
            DomainIntelligence = baseline.DomainIntelligence! with { CatchAll = candidate },
            CatchAllEvidence = candidate,
            MxValidation = new MxValidationEvidence([], [], MxConsensus.Conflicting),
            ReasonCodes = [ReasonCode.MxResultsConflicting, ReasonCode.ProviderEvidenceConflicting]
        };

        var snapshot = await factory.CreateAsync(result, new EmailValidationRequest(
            ValidationId: "validation-ambiguity", TenantId: "tenant-1"));

        Assert.NotNull(snapshot);
        Assert.Equal(DomainRecipientBehavior.Unknown, snapshot.Domain.RecipientBehavior);
        Assert.True(snapshot.Domain.AcceptAllCandidate);
        Assert.True(snapshot.Domain.MxEvidenceConflicting);
        Assert.True(snapshot.Domain.ProviderEvidenceConflicting);
        var ambiguityFeatures = LogisticFeatureEncoder.Encode(snapshot);
        Assert.Equal(1d, ambiguityFeatures["accept_all_candidate"]);
        Assert.Equal(1d, ambiguityFeatures["mx_evidence_conflicting"]);
        Assert.Equal(1d, ambiguityFeatures["provider_evidence_conflicting"]);

        var recipientSpecific = snapshot with
        {
            Domain = snapshot.Domain with
            {
                RecipientBehavior = DomainRecipientBehavior.RecipientSpecific,
                AcceptAllCandidate = false
            }
        };
        var catchAll = snapshot with
        {
            Domain = snapshot.Domain with
            {
                RecipientBehavior = DomainRecipientBehavior.CatchAll,
                AcceptAllCandidate = false
            }
        };
        var recipientSpecificFeatures = LogisticFeatureEncoder.Encode(recipientSpecific);
        var catchAllFeatures = LogisticFeatureEncoder.Encode(catchAll);
        Assert.Equal(1d, recipientSpecificFeatures["recipient_behavior_recipient_specific"]);
        Assert.Equal(0d, recipientSpecificFeatures["recipient_behavior_catch_all"]);
        Assert.Equal(0d, catchAllFeatures["recipient_behavior_recipient_specific"]);
        Assert.Equal(1d, catchAllFeatures["recipient_behavior_catch_all"]);
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
            ValidationId = snapshot1.ValidationId,
            SendAttemptAtUtc = snapshotAt.AddDays(-2),
            ObservedAtUtc = snapshotAt.AddDays(-1)
        });
        await store.AppendAsync(Outcome("label-1", EmailDeliveryOutcome.Delivered) with
        {
            EmailCorrelationId = "email-1",
            ValidationId = snapshot1.ValidationId,
            SendAttemptAtUtc = snapshotAt.AddDays(1),
            ObservedAtUtc = snapshotAt.AddDays(2)
        });
        await store.AppendAsync(Outcome("label-2a", EmailDeliveryOutcome.Delivered) with
        {
            EmailCorrelationId = "email-2",
            ValidationId = snapshot2.ValidationId,
            SendAttemptAtUtc = snapshotAt.AddDays(3),
            ObservedAtUtc = snapshotAt.AddDays(4)
        });
        await store.AppendAsync(Outcome("label-2b", EmailDeliveryOutcome.HardBounce) with
        {
            EmailCorrelationId = "email-2",
            ValidationId = snapshot2.ValidationId,
            SendAttemptAtUtc = snapshotAt.AddDays(3),
            ObservedAtUtc = snapshotAt.AddDays(4)
        });
        var request = new TrainingDatasetRequest(
            PredictionTargetKind.TechnicalDeliveryWithinWindow,
            "delivery-7d-v1",
            EvidenceBackedClassificationVersions.FeatureSchemaV2,
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
    public async Task MailboxDataset_UsesLatestExactValidationAndExcludesAmbiguousLabels()
    {
        using var store = Store();
        var metrics = new RecordingMetrics();
        var snapshotAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sendAt = snapshotAt.AddDays(5);
        var early = Snapshot("early", "shared-email", "domain", snapshotAt, MailProvider.GenericSmtp) with
        {
            ValidationId = "validation-shared",
            Operational = Snapshot("unused", "email", "domain", snapshotAt, MailProvider.GenericSmtp).Operational with
            {
                AttemptNumber = 1
            }
        };
        var latest = early with
        {
            SnapshotId = "latest",
            SnapshotAtUtc = snapshotAt.AddDays(2),
            Operational = early.Operational with { AttemptNumber = 2 }
        };
        var mismatched = Snapshot("mismatch", "mismatch-email", "domain", snapshotAt, MailProvider.GenericSmtp);
        var candidate = NonDiscriminatingSnapshot(
            Snapshot("candidate", "candidate-email", "domain", snapshotAt, MailProvider.GenericSmtp),
            DomainRecipientBehavior.Unknown,
            acceptAllCandidate: true);
        var acceptAll = NonDiscriminatingSnapshot(
            Snapshot("accept-all", "accept-all-email", "domain", snapshotAt, MailProvider.GenericSmtp),
            DomainRecipientBehavior.AcceptAll);
        var catchAll = NonDiscriminatingSnapshot(
            Snapshot("catch-all", "catch-all-email", "domain", snapshotAt, MailProvider.GenericSmtp),
            DomainRecipientBehavior.CatchAll) with
        {
            HeuristicStatus = EmailValidationStatus.CatchAll
        };
        var gateway = NonDiscriminatingSnapshot(
            Snapshot("gateway", "gateway-email", "domain", snapshotAt, MailProvider.GoogleWorkspace),
            DomainRecipientBehavior.Unknown) with
        {
            Smtp = Snapshot("gateway-source", "email", "domain", snapshotAt, MailProvider.GoogleWorkspace).Smtp with
            {
                Category = SmtpResponseCategory.GatewayAccepted,
                RecipientAccepted = false
            }
        };
        var mailboxFull = Snapshot("mailbox-full", "full-email", "domain", snapshotAt, MailProvider.GenericSmtp) with
        {
            HeuristicStatus = EmailValidationStatus.Risky,
            Smtp = Snapshot("full-source", "email", "domain", snapshotAt, MailProvider.GenericSmtp).Smtp with
            {
                Category = SmtpResponseCategory.MailboxFull,
                MailboxFull = true
            }
        };
        var genericBounce = Snapshot("generic-bounce", "generic-bounce-email", "domain", snapshotAt,
            MailProvider.GenericSmtp);
        var generic5110Bounce = Snapshot("generic-5110-bounce", "generic-5110-email", "domain", snapshotAt,
            MailProvider.GenericSmtp);
        var recipientBounce = Snapshot("recipient-bounce", "recipient-bounce-email", "domain", snapshotAt,
            MailProvider.GenericSmtp);
        EmailValidationFeatureSnapshot[] allSnapshots =
        [
            early, latest, mismatched, candidate, acceptAll, catchAll, gateway, mailboxFull,
            genericBounce, generic5110Bounce, recipientBounce
        ];
        foreach (var snapshot in allSnapshots)
            await ((IEmailValidationFeatureSnapshotStore)store).AppendAsync(snapshot);

        await store.AppendAsync(OutcomeFor(latest, "latest-delivered", EmailDeliveryOutcome.Delivered, sendAt));
        await store.AppendAsync(OutcomeFor(mismatched, "wrong-validation", EmailDeliveryOutcome.Delivered, sendAt) with
        {
            ValidationId = "different-validation"
        });
        foreach (var snapshot in new[] { candidate, acceptAll, catchAll, gateway, mailboxFull })
            await store.AppendAsync(OutcomeFor(
                snapshot, $"{snapshot.SnapshotId}-delivered", EmailDeliveryOutcome.Delivered, sendAt));
        await store.AppendAsync(OutcomeFor(
            genericBounce, "generic-hard-bounce", EmailDeliveryOutcome.HardBounce, sendAt));
        await store.AppendAsync(OutcomeFor(
            generic5110Bounce, "generic-5110-hard-bounce", EmailDeliveryOutcome.HardBounce, sendAt) with
        {
            EnhancedStatusCode = "5.1.10"
        });
        await store.AppendAsync(OutcomeFor(
            recipientBounce, "recipient-hard-bounce", EmailDeliveryOutcome.HardBounce, sendAt) with
        {
            EnhancedStatusCode = "5.1.1"
        });

        var dataset = await new TrainingDatasetBuilder(
            store, store, new OutcomeDefinitionCatalog(), metrics, new FixedTimeProvider(snapshotAt.AddDays(21)))
            .BuildAsync(new TrainingDatasetRequest(
                PredictionTargetKind.MailboxExistence,
                EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV2,
                EvidenceBackedClassificationVersions.FeatureSchemaV2,
                snapshotAt.AddDays(-1),
                snapshotAt.AddDays(10),
                snapshotAt.AddDays(20)));

        Assert.Collection(
            dataset.Rows.Select(row => row.SnapshotId).OrderBy(id => id, StringComparer.Ordinal),
            id => Assert.Equal("latest", id),
            id => Assert.Equal("mailbox-full", id),
            id => Assert.Equal("recipient-bounce", id));
        Assert.Equal(2, dataset.Manifest.PositiveCount);
        Assert.Equal(1, dataset.Manifest.NegativeCount);
        Assert.Equal(6, dataset.Manifest.ExcludedCount);
        Assert.Equal(2, dataset.Manifest.UnresolvedCount);
        Assert.DoesNotContain(dataset.Rows, row => row.SnapshotId == "early");
        Assert.DoesNotContain(dataset.Rows, row => row.SnapshotId == "generic-5110-bounce");
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
            "empty", DateTimeOffset.UtcNow, EvidenceBackedClassificationVersions.FeatureSchemaV2,
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
    public void DecisionPolicy_ProtectsDeterministicInvalid_AndLeavesHeuristicOnAbstention()
    {
        var options = Options.Create(new EmailValidationOptions());
        var policy = new VersionedValidationDecisionPolicy(options);
        var invalid = Result("person@example.test", DateTimeOffset.UtcNow) with
        {
            Status = EmailValidationStatus.Invalid
        };
        var model = new PredictionModelMetadata("baseline", "1", EvidenceBackedClassificationVersions.FeatureSchemaV2,
            "platt-1", EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV2, "policy-1",
            DateTimeOffset.UtcNow, "dataset", "checksum",
            DateTimeOffset.UtcNow, ModelRolloutMode.Enforced);
        var prediction = new CalibratedPrediction(PredictionTargetKind.MailboxExistence, 0.99, model);

        Assert.True(policy.Decide(invalid, prediction,
            new PredictionUncertainty(PredictionDisposition.AcceptedPrediction, "supported")).DeterministicOverride);
        Assert.Equal(EmailValidationStatus.Risky, policy.Decide(
            invalid with { Status = EmailValidationStatus.Risky }, prediction,
            new PredictionUncertainty(PredictionDisposition.Abstain, "near threshold")).Status);

        var acceptAllCandidate = invalid with
        {
            Status = EmailValidationStatus.Unknown,
            CatchAllEvidence = new CatchAllDetectionResult(
                CatchAllStatus.Unknown, 2, 2, 0, 0, "Candidate", .70)
            {
                ReasonCode = CatchAllReasonCode.AcceptAllCandidate
            }
        };
        var protectedCandidate = policy.Decide(
            acceptAllCandidate,
            prediction,
            new PredictionUncertainty(PredictionDisposition.AcceptedPrediction, "supported"));
        Assert.Equal(EmailValidationStatus.Unknown, protectedCandidate.Status);
        Assert.True(protectedCandidate.DeterministicOverride);
    }

    [Fact]
    public void DecisionPolicy_NonMailboxTargetCannotChangeCanonicalMailboxStatus()
    {
        var policy = new VersionedValidationDecisionPolicy(Options.Create(new EmailValidationOptions()));
        var heuristic = Result("person@example.test", DateTimeOffset.UtcNow) with
        {
            Status = EmailValidationStatus.Unknown
        };
        var model = new PredictionModelMetadata(
            "hard-bounce", "1", EvidenceBackedClassificationVersions.FeatureSchemaV2,
            "platt-1", "hard-bounce-7d-v1", "policy-1", DateTimeOffset.UtcNow,
            "dataset", "checksum", DateTimeOffset.UtcNow, ModelRolloutMode.Enforced);

        var decision = policy.Decide(
            heuristic,
            new CalibratedPrediction(PredictionTargetKind.HardBounceWithinWindow, 0.99, model),
            new PredictionUncertainty(PredictionDisposition.AcceptedPrediction, "supported"));

        Assert.Equal(EmailValidationStatus.Unknown, decision.Status);
        Assert.True(decision.DeterministicOverride);
    }

    [Fact]
    public void DecisionPolicy_CatchAllWithTransientSmtp_RemainsUnknownDespiteExtremePrediction()
    {
        var policy = new VersionedValidationDecisionPolicy(Options.Create(new EmailValidationOptions()));
        var heuristic = Result("person@example.test", DateTimeOffset.UtcNow) with
        {
            Status = EmailValidationStatus.Unknown,
            CatchAllEvidence = new CatchAllDetectionResult(
                CatchAllStatus.LikelyCatchAll, 2, 2, 0, 0,
                "Independent routing evidence confirms catch-all delivery.", .96)
            {
                RecipientBehavior = DomainRecipientBehavior.CatchAll,
                ReasonCode = CatchAllReasonCode.IndependentRoutingEvidence
            },
            ProviderValidation = new ProviderValidationResult(
                MailProvider.GenericSmtp,
                .80,
                SmtpResponseCategory.TemporaryFailure,
                AcceptanceStrength.None,
                [ReasonCode.TemporarySmtpFailure],
                "The destination returned a transient SMTP failure.")
        };
        var model = new PredictionModelMetadata(
            "mailbox-existence", "1", EvidenceBackedClassificationVersions.FeatureSchemaV2,
            "platt-1", EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV2, "policy-2",
            DateTimeOffset.UtcNow, "dataset", "checksum",
            DateTimeOffset.UtcNow, ModelRolloutMode.Enforced);

        var decision = policy.Decide(
            heuristic,
            new CalibratedPrediction(PredictionTargetKind.MailboxExistence, .99, model),
            new PredictionUncertainty(PredictionDisposition.AcceptedPrediction, "supported"));

        Assert.Equal(EmailValidationStatus.Unknown, decision.Status);
        Assert.True(decision.DeterministicOverride);
    }

    [Fact]
    public void DecisionPolicy_TypoRiskCannotBeErasedByMailboxExistencePrediction()
    {
        var policy = new VersionedValidationDecisionPolicy(Options.Create(new EmailValidationOptions()));
        var heuristic = Result("person@exampel.test", DateTimeOffset.UtcNow) with
        {
            Status = EmailValidationStatus.Risky,
            Confidence = .73,
            ReasonCodes = [ReasonCode.TypoDetected, ReasonCode.SuggestedDomainCorrection],
            Recommendation = new SendRecommendation(null, RecommendationRisk.Moderate, ["TypoDetected"])
        };
        var model = new PredictionModelMetadata(
            "mailbox-existence", "1", EvidenceBackedClassificationVersions.FeatureSchemaV2,
            "platt-1", EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV2, "policy-2",
            DateTimeOffset.UtcNow, "dataset", "checksum",
            DateTimeOffset.UtcNow, ModelRolloutMode.Enforced);
        var calibrated = new CalibratedPrediction(PredictionTargetKind.MailboxExistence, .99, model);

        var decision = policy.Decide(
            heuristic,
            calibrated,
            new PredictionUncertainty(PredictionDisposition.AcceptedPrediction, "supported"));
        var prediction = new EmailValidationPrediction
        {
            HeuristicEvidenceStrength = heuristic.Confidence,
            MailboxExistenceProbability = calibrated.Probability,
            VerificationReliability = .8,
            Uncertainty = new PredictionUncertainty(PredictionDisposition.AcceptedPrediction, "supported"),
            Model = model,
            Decision = decision
        };
        var projected = EnforcedValidationResultProjection.Apply(
            heuristic with { Prediction = prediction }, prediction);

        Assert.Equal(EmailValidationStatus.Risky, decision.Status);
        Assert.True(decision.DeterministicOverride);
        Assert.Equal(EmailValidationStatus.Risky, projected.Status);
        Assert.Equal(.73, projected.Confidence);
        Assert.Null(projected.Recommendation?.Send);
        Assert.Contains(ReasonCode.TypoDetected, projected.ReasonCodes);
        Assert.Contains(ReasonCode.SuggestedDomainCorrection, projected.ReasonCodes);
    }

    [Fact]
    public void EnforcedMailboxProjection_UpdatesCanonicalMetadataAndPreservesIndependentRisk()
    {
        var riskEvidence = new EvidenceProvenance(
            "Suppression", EvidenceSource.ConfiguredIntelligenceProvider, 0.99, "Known suppression.");
        var heuristic = Result("person@example.test", DateTimeOffset.UtcNow) with
        {
            Status = EmailValidationStatus.Unknown,
            Confidence = 0.72,
            ConfidenceType = ConfidenceType.Heuristic,
            ConfidenceReason = "SMTP timed out.",
            UnknownContext = new UnknownValidationContext(
                UnknownCause.SmtpTimeout, "Timed out.", true, "Retry.", SmtpResponseCategory.Timeout),
            DetailedStatus = DetailedStatus.Timeout,
            DetailedStatuses = [DetailedStatus.Timeout],
            SubStatus = DetailedStatus.Timeout,
            SubStatuses = [DetailedStatus.Timeout],
            Recommendation = new SendRecommendation(false, RecommendationRisk.High, ["Suppression"]),
            MailingRisk = new EmailRiskResult(
                EmailValidationStatus.Unknown, 0.72, MailingRiskLevel.High,
                [MailingRiskReason.KnownSuppression], [riskEvidence])
        };
        var model = new PredictionModelMetadata(
            "mailbox-existence", "1", EvidenceBackedClassificationVersions.FeatureSchemaV2,
            "platt-1", EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV2, "policy-2",
            DateTimeOffset.UtcNow, "dataset", "checksum", DateTimeOffset.UtcNow, ModelRolloutMode.Enforced);
        var prediction = new EmailValidationPrediction
        {
            HeuristicEvidenceStrength = heuristic.Confidence,
            MailboxExistenceProbability = 0.91,
            VerificationReliability = 0.8,
            Uncertainty = new PredictionUncertainty(PredictionDisposition.AcceptedPrediction, "supported"),
            Model = model,
            Decision = new ValidationDecision(
                EmailValidationStatus.LikelyValid,
                "Calibrated probability exceeds the likely-valid threshold.")
        };

        var projected = EnforcedValidationResultProjection.Apply(
            heuristic with { Prediction = prediction }, prediction);

        Assert.Equal(EmailValidationStatus.LikelyValid, projected.Status);
        Assert.Equal(0.91, projected.Confidence);
        Assert.Equal(ConfidenceType.CalibratedProbability, projected.ConfidenceType);
        Assert.Equal(prediction.Decision.Reason, projected.ConfidenceReason);
        Assert.Null(projected.UnknownContext);
        Assert.Equal(DetailedStatus.Unknown, projected.DetailedStatus);
        Assert.Empty(projected.DetailedStatuses);
        Assert.Equal(DetailedStatus.Unknown, projected.SubStatus);
        Assert.Empty(projected.SubStatuses);
        Assert.False(projected.Recommendation?.Send);
        Assert.Equal(EmailValidationStatus.LikelyValid, projected.MailingRisk?.DeliverabilityStatus);
        Assert.Equal(0.91, projected.MailingRisk?.DeliverabilityConfidence);
        Assert.Equal(MailingRiskLevel.High, projected.MailingRisk?.MailingRisk);
        Assert.Equal([MailingRiskReason.KnownSuppression], projected.MailingRisk?.RiskReasons);
        Assert.Equal([riskEvidence], projected.MailingRisk?.Evidence);
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
                FeatureSchemaVersion = EvidenceBackedClassificationVersions.FeatureSchemaV2,
                CalibrationVersion = "platt-1",
                OutcomeDefinitionVersion = EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV2,
                TrainingDataCutoffUtc = DateTimeOffset.UtcNow.AddDays(-1),
                TrainingDatasetId = "dataset-1",
                Intercept = -0.5,
                Coefficients = LogisticFeatureEncoder.SupportedFeatures.ToDictionary(
                    name => name,
                    name => name == "heuristic_evidence_strength" ? 2d : 0d,
                    StringComparer.Ordinal),
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

    [Fact]
    public void LogisticArtifact_RejectsV1AndIncompleteV2FeatureContracts()
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
                OutcomeDefinitionVersion = EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV2,
                TrainingDataCutoffUtc = DateTimeOffset.UtcNow.AddDays(-1),
                TrainingDatasetId = "dataset-legacy",
                Intercept = 0,
                Coefficients = LogisticFeatureEncoder.SupportedFeatures.ToDictionary(
                    name => name, _ => 0d, StringComparer.Ordinal),
                CalibrationSlope = 1,
                CalibrationIntercept = 0,
                L2Regularization = 0.01,
                RandomSeed = 17
            };

            AssertArtifactRejected(path, artifact);
            AssertArtifactRejected(path, artifact with
            {
                FeatureSchemaVersion = EvidenceBackedClassificationVersions.FeatureSchemaV2,
                Coefficients = new Dictionary<string, double>
                {
                    ["heuristic_evidence_strength"] = 1
                }
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertArtifactRejected(string path, LogisticRegressionArtifact artifact)
    {
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
        Assert.Throws<InvalidDataException>(() => scorer.Score(
            Snapshot("rejected-artifact", "email", "domain", DateTimeOffset.UtcNow,
                MailProvider.GenericSmtp)));
    }

    private static EmailValidationFeatureSnapshot NonDiscriminatingSnapshot(
        EmailValidationFeatureSnapshot snapshot,
        DomainRecipientBehavior behavior,
        bool acceptAllCandidate = false) => snapshot with
        {
            Domain = snapshot.Domain with
            {
                CatchAllState = behavior == DomainRecipientBehavior.CatchAll
                    ? CatchAllStatus.LikelyCatchAll
                    : CatchAllStatus.Unknown,
                RecipientBehavior = behavior,
                AcceptAllCandidate = acceptAllCandidate
            },
            Smtp = snapshot.Smtp with
            {
                Category = SmtpResponseCategory.Accepted,
                RecipientAccepted = true
            }
        };

    private static EmailDeliveryOutcomeObservation OutcomeFor(
        EmailValidationFeatureSnapshot snapshot,
        string eventId,
        EmailDeliveryOutcome outcome,
        DateTimeOffset sendAt) => new()
        {
            OutcomeEventId = eventId,
            EmailCorrelationId = snapshot.EmailCorrelationId,
            ValidationId = snapshot.ValidationId,
            Outcome = outcome,
            Confidence = OutcomeConfidence.Authoritative,
            OutcomeSource = "provider-webhook",
            SourceEventId = eventId,
            Provider = snapshot.Domain.Provider,
            SendAttemptAtUtc = sendAt,
            ObservedAtUtc = sendAt.AddHours(1),
            NormalizationVersion = "delivery-outcome-normalization-v2-recipient-specific-hard-bounce"
        };

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
                SyntaxValid = true,
                DomainExists = true,
                MxPresent = true,
                CatchAll = CatchAllStatus.NotCatchAll,
                Mailbox = SmtpMailboxStatus.Accepted
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
            FeatureSchemaVersion = EvidenceBackedClassificationVersions.FeatureSchemaV2,
            Syntax = new(true, true, false, false, false, false),
            Domain = new(true, DnsStatus.Success, true, false, 1, false, provider, 0.8,
                DnsSecurityState.Unknown, AuthenticationRecordState.Unknown, AuthenticationRecordState.Unknown,
                CatchAllStatus.NotCatchAll, 0.8, "mx")
            {
                RecipientBehavior = DomainRecipientBehavior.RecipientSpecific
            },
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
                "platt-1", EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV2, "policy-1",
                DateTimeOffset.UtcNow,
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
