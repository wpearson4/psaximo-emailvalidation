using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed record BenchmarkEvidenceBundle(TrainingDatasetRequest Request,
    DateTimeOffset CalibrationStartsUtc, DateTimeOffset TestStartsUtc, BenchmarkPolicy Policy,
    IReadOnlyList<EmailValidationFeatureSnapshot> Snapshots,
    IReadOnlyList<EmailDeliveryOutcomeObservation> Outcomes)
{
    public IReadOnlyList<BenchmarkMeasurement> Measurements { get; init; } = [];
    public ClassificationModelOptions? ModelPolicy { get; init; }
}

public static class OfflineAccuracyBenchmark
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static async Task<AccuracyBenchmarkReport> RunAsync(string inputPath, string outputPath,
        string? candidateArtifactPath = null, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        var input = JsonSerializer.Deserialize<BenchmarkEvidenceBundle>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Benchmark input is empty.");
        if (input.Request.OutcomeDefinitionVersion != EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV3 ||
            input.Request.Target != PredictionTargetKind.MailboxExistence)
            throw new InvalidDataException("The benchmark requires the authorized mailbox-existence v3 definition.");
        if (input.Snapshots.Select(s => s.SnapshotId).Distinct().Count() != input.Snapshots.Count ||
            input.Outcomes.Select(o => o.OutcomeEventId).Distinct().Count() != input.Outcomes.Count ||
            input.Outcomes.Any(o => o.Cohort != input.Request.Cohort))
            throw new InvalidDataException("Duplicate IDs or mixed evidence cohorts are not allowed in a frozen input.");
        using var store = new LocalClassificationEvidenceStore(Options.Create(new EmailValidationOptions { Persistence = new() { Enabled = false } }));
        using var metrics = new ClassificationFoundationMetrics();
        foreach (var snapshot in input.Snapshots)
            await ((IEmailValidationFeatureSnapshotStore)store).AppendAsync(snapshot, cancellationToken);
        var ingestion = new EmailDeliveryOutcomeIngestionService(store, metrics);
        foreach (var outcome in input.Outcomes)
        {
            var result = await ingestion.IngestAsync(outcome, cancellationToken);
            if (result.RejectionReason is not null) throw new InvalidDataException(result.RejectionReason);
        }
        var builder = new TrainingDatasetBuilder(store, store, new OutcomeDefinitionCatalog(), metrics,
            new FrozenClock(input.Request.MaturationCutoffUtc));
        var dataset = await builder.BuildAsync(input.Request, cancellationToken);
        var checksum = "heuristic-baseline";
        LogisticRegressionProbabilityScorer? scorer = null;
        PlattProbabilityCalibrator? calibrator = null;
        if (candidateArtifactPath is not null)
        {
            checksum = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(candidateArtifactPath, cancellationToken))).ToLowerInvariant();
            var provider = new LogisticRegressionArtifactProvider(Options.Create(new EmailValidationOptions
            {
                ClassificationModel = new() { Mode = ModelRolloutMode.Shadow, ArtifactPath = candidateArtifactPath, ArtifactChecksum = checksum }
            }));
            if (provider.Get().Artifact.OutcomeDefinitionVersion != input.Request.OutcomeDefinitionVersion)
                throw new InvalidDataException("Candidate and benchmark outcome definitions differ.");
            scorer = new(provider); calibrator = new(provider);
        }
        var modelOptions = Options.Create(new EmailValidationOptions { ClassificationModel = input.ModelPolicy ?? new() });
        var decisionPolicy = new VersionedValidationDecisionPolicy(modelOptions);
        var uncertainty = new TransparentPredictionUncertaintyPolicy(modelOptions);
        var predictions = dataset.Rows.Select(row =>
        {
            if (scorer is null) return new BenchmarkPrediction(row.SnapshotId, row.Snapshot.HeuristicStatus);
            var prediction = calibrator!.Calibrate(scorer.Score(row.Snapshot));
            var support = uncertainty.Evaluate(prediction, row.Snapshot);
            var heuristic = new EmailValidationResult
            {
                Email = "redacted@example.test", Status = row.Snapshot.HeuristicStatus, Confidence = row.Snapshot.HeuristicEvidenceStrength,
                Checks = new(), CatchAllEvidence = new CatchAllDetectionResult(CatchAllStatus.Unknown, 0, 0, 0, 0)
                {
                    RecipientBehavior = row.Snapshot.Domain.RecipientBehavior
                }
            };
            // Preserve endpoint ambiguity even when no full runtime result is available in the frozen features.
            var ambiguous = row.Snapshot.Domain.RecipientBehavior is DomainRecipientBehavior.CatchAll or DomainRecipientBehavior.AcceptAll ||
                row.Snapshot.Smtp.RecipientAccepted && row.Snapshot.Domain.RecipientBehavior != DomainRecipientBehavior.RecipientSpecific ||
                row.Snapshot.Domain.AcceptAllCandidate || row.Snapshot.Domain.MxEvidenceConflicting ||
                row.Snapshot.Domain.ProviderEvidenceConflicting || row.Snapshot.Smtp.Category == SmtpResponseCategory.GatewayAccepted;
            var decision = ambiguous ? new ValidationDecision(heuristic.Status, "Frozen endpoint ambiguity") : decisionPolicy.Decide(heuristic, prediction, support);
            return new BenchmarkPrediction(row.SnapshotId, decision.Status,
                support.Disposition is PredictionDisposition.OutOfDistribution or PredictionDisposition.InsufficientSupport ? null : prediction.Probability);
        }).ToArray();
        var report = AccuracyBenchmark.Evaluate(dataset, predictions, input.Policy, input.CalibrationStartsUtc,
            input.TestStartsUtc, checksum, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
            with { ModelPolicyHash = ModelReleaseGate.PolicyHash(modelOptions.Value.ClassificationModel),
                Supplemental = BenchmarkSupplementalMetrics.Calculate(input.Snapshots, input.Measurements) };
        // Stable file contents: the dataset clock is the declared frozen outcome cutoff.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(outputPath + ".dataset.json", JsonSerializer.Serialize(dataset, JsonOptions), cancellationToken);
        return report;
    }
    private sealed class FrozenClock(DateTimeOffset at) : TimeProvider { public override DateTimeOffset GetUtcNow() => at; }
}
