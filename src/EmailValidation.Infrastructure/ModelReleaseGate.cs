using System.Security.Cryptography;
using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

public sealed record ModelReleaseApproval(
    string ApprovalId, string ApprovedBy, DateTimeOffset ApprovedAtUtc, DateTimeOffset ExpiresAtUtc,
    string ArtifactChecksum, string DatasetPath, string DatasetChecksum,
    string EvaluationPath, string EvaluationChecksum, string CalibrationPath, string CalibrationChecksum,
    string DecisionPolicyVersion, IReadOnlyList<MailProvider> SupportedProviders,
    int MinimumPositivePerProvider, int MinimumNegativePerProvider,
    double MaximumFalsePositiveRate, double MaximumFalseNegativeRate, double MaximumPairedRegression,
    string RollbackArtifactChecksum);
public sealed record CalibrationEvidence(string DatasetId, string CalibrationVersion,
    IReadOnlyList<string> SnapshotIds, IReadOnlyList<string> TrainingSnapshotIds);

/// <summary>Operator-approved, hash-bound release evidence. Shadow artifacts do not require approval.</summary>
public static class ModelReleaseGate
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public static ModelReleaseApproval Validate(ClassificationModelOptions options,
        LogisticRegressionArtifact artifact, string artifactChecksum)
    {
        var approval = Read<ModelReleaseApproval>(options.ReleaseApprovalPath, options.ReleaseApprovalChecksum);
        var root = Path.GetDirectoryName(Path.GetFullPath(options.ReleaseApprovalPath))!;
        var dataset = Read<TrainingDataset>(Path.Combine(root, approval.DatasetPath), approval.DatasetChecksum);
        var evaluation = Read<AccuracyBenchmarkReport>(Path.Combine(root, approval.EvaluationPath), approval.EvaluationChecksum);
        var calibration = Read<CalibrationEvidence>(Path.Combine(root, approval.CalibrationPath), approval.CalibrationChecksum);
        if (string.IsNullOrWhiteSpace(approval.ApprovalId) || string.IsNullOrWhiteSpace(approval.ApprovedBy) ||
            approval.ApprovedAtUtc > DateTimeOffset.UtcNow || approval.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            approval.ApprovedAtUtc == default || approval.ArtifactChecksum != artifactChecksum ||
            !HashString(approval.RollbackArtifactChecksum) || approval.DecisionPolicyVersion != options.DecisionPolicyVersion ||
            approval.SupportedProviders is not { Count: > 0 } || approval.SupportedProviders.Contains(MailProvider.Unknown) ||
            approval.SupportedProviders.Any(p => !Enum.IsDefined(p)) ||
            approval.SupportedProviders.Except(artifact.SupportedProviders).Any() ||
            approval.MinimumPositivePerProvider < 1 || approval.MinimumNegativePerProvider < 1 ||
            !Rate(approval.MaximumFalsePositiveRate) || !Rate(approval.MaximumFalseNegativeRate) || !Rate(approval.MaximumPairedRegression))
            throw new InvalidDataException("Model release approval is missing, expired, mismatched, or has invalid gates.");
        if (dataset.Manifest.Cohort != EvidenceCohort.AuthorizedReal || evaluation.Cohort != EvidenceCohort.AuthorizedReal ||
            dataset.Rows.Any(r => r.Snapshot.PolicyVersions is null) || evaluation.BaselinePolicyVersions.Count == 0 ||
            dataset.Manifest.DatasetId != artifact.TrainingDatasetId || evaluation.DatasetId != artifact.TrainingDatasetId ||
            evaluation.DatasetHash != dataset.Manifest.DatasetHash || evaluation.CandidateChecksum != artifactChecksum ||
            evaluation.FeatureSchemaVersion != artifact.FeatureSchemaVersion || evaluation.OutcomeDefinitionVersion != artifact.OutcomeDefinitionVersion ||
            artifact.OutcomeDefinitionVersion != EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV3 ||
            evaluation.TrainingRows < 1 || evaluation.CalibrationRows < 2 || evaluation.ModelPolicyHash != PolicyHash(options) ||
            calibration.DatasetId != dataset.Manifest.DatasetId || calibration.CalibrationVersion != artifact.CalibrationVersion)
            throw new InvalidDataException("Dataset, calibration, and held-out evaluation do not support this release.");
        var split = LeakageSafeDatasetSplitter.Split(dataset.Rows, evaluation.CalibrationStartsUtc, evaluation.TestStartsUtc);
        var allowedTraining = split.Training.Where(r => (r.LabelMaturedAtUtc ?? r.OutcomeObservedAtUtc) < evaluation.CalibrationStartsUtc).ToArray();
        if (artifact.TrainingDataCutoffUtc >= evaluation.CalibrationStartsUtc ||
            calibration.TrainingSnapshotIds.Count < 2 || calibration.TrainingSnapshotIds.Distinct().Count() != calibration.TrainingSnapshotIds.Count ||
            calibration.TrainingSnapshotIds.Except(allowedTraining.Select(r => r.SnapshotId)).Any() ||
            allowedTraining.Where(r => calibration.TrainingSnapshotIds.Contains(r.SnapshotId)).Select(r => r.Label).Distinct().Count() != 2)
            throw new InvalidDataException("Training evidence crosses a holdout boundary or lacks both labels.");
        var allowedCalibration = split.Calibration.Where(r => (r.LabelMaturedAtUtc ?? r.OutcomeObservedAtUtc) < evaluation.TestStartsUtc).ToArray();
        if (calibration.SnapshotIds.Count < 2 || calibration.SnapshotIds.Distinct().Count() != calibration.SnapshotIds.Count ||
            calibration.SnapshotIds.Except(allowedCalibration.Select(r => r.SnapshotId)).Any() ||
            allowedCalibration.Where(r => calibration.SnapshotIds.Contains(r.SnapshotId)).Select(r => r.Label).Distinct().Count() != 2)
            throw new InvalidDataException("Calibration evidence leaks outside the held-out calibration partition or lacks both labels.");
        Check(evaluation.OutOfTime, approval);
        Check(evaluation.UnseenDomain, approval);
        foreach (var provider in approval.SupportedProviders)
        {
            if (!evaluation.Providers.TryGetValue(provider, out var comparison))
                throw new InvalidDataException("A supported provider has no held-out evaluation.");
            Check(comparison, approval);
        }
        return approval;
    }
    private static void Check(BenchmarkComparison comparison, ModelReleaseApproval approval)
    {
        var c = comparison.Candidate;
        if (c.TruthPositive < approval.MinimumPositivePerProvider || c.TruthNegative < approval.MinimumNegativePerProvider ||
            c.FalsePositiveRate is not { } fp || fp > approval.MaximumFalsePositiveRate ||
            c.FalseNegativeRate is not { } fn || fn > approval.MaximumFalseNegativeRate ||
            comparison.FalsePositiveRateChange.Upper is not { } fpChange || fpChange > approval.MaximumPairedRegression ||
            comparison.FalseNegativeRateChange.Upper is not { } fnChange || fnChange > approval.MaximumPairedRegression ||
            c.BrierScore is null || c.LogLoss is null)
            throw new InvalidDataException("Held-out support, probability evaluation, or paired regression gates failed.");
    }
    public static T Read<T>(string path, string checksum)
    {
        if (string.IsNullOrWhiteSpace(path) || !HashString(checksum)) throw new InvalidDataException("A path and SHA-256 checksum are required.");
        var bytes = File.ReadAllBytes(path);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(checksum)))
            throw new InvalidDataException("Release evidence checksum mismatch.");
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Release evidence is empty.");
    }
    public static string PolicyHash(ClassificationModelOptions options) => AccuracyBenchmark.Hash(new
    {
        options.DecisionPolicyVersion, options.LikelyValidThreshold, options.LikelyInvalidThreshold,
        options.AbstentionLowerBound, options.AbstentionUpperBound, options.MinimumVerificationReliability,
        options.MaximumMissingFeatureFraction
    });
    private static bool HashString(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Rate(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
