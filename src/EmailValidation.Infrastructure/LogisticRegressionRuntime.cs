using System.Security.Cryptography;
using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed record LogisticRegressionArtifact
{
    public required string ModelName { get; init; }
    public required string ModelVersion { get; init; }
    public required PredictionTargetKind Target { get; init; }
    public required string FeatureSchemaVersion { get; init; }
    public required string CalibrationVersion { get; init; }
    public required string OutcomeDefinitionVersion { get; init; }
    public required DateTimeOffset TrainingDataCutoffUtc { get; init; }
    public required string TrainingDatasetId { get; init; }
    public required double Intercept { get; init; }
    public required IReadOnlyDictionary<string, double> Coefficients { get; init; }
    public required double CalibrationSlope { get; init; }
    public required double CalibrationIntercept { get; init; }
    public required double L2Regularization { get; init; }
    public required int RandomSeed { get; init; }
}

public sealed class LogisticRegressionArtifactProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ClassificationModelOptions _options;
    private readonly Lazy<(LogisticRegressionArtifact Artifact, string Checksum)> _artifact;

    public LogisticRegressionArtifactProvider(IOptions<EmailValidationOptions> options)
    {
        _options = options.Value.ClassificationModel;
        _artifact = new Lazy<(LogisticRegressionArtifact, string)>(Load,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public (LogisticRegressionArtifact Artifact, string Checksum) Get() => _artifact.Value;

    private (LogisticRegressionArtifact Artifact, string Checksum) Load()
    {
        if (string.IsNullOrWhiteSpace(_options.ArtifactPath) || string.IsNullOrWhiteSpace(_options.ArtifactChecksum))
            throw new InvalidOperationException("No approved classification model artifact is configured.");
        var path = Path.GetFullPath(_options.ArtifactPath);
        if (!File.Exists(path)) throw new FileNotFoundException("Approved classification model artifact is missing.", path);
        var bytes = File.ReadAllBytes(path);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(checksum), Convert.FromHexString(_options.ArtifactChecksum)))
            throw new InvalidDataException("Classification model artifact checksum does not match configuration.");
        var artifact = JsonSerializer.Deserialize<LogisticRegressionArtifact>(bytes, JsonOptions) ??
            throw new InvalidDataException("Classification model artifact is not valid JSON metadata.");
        Validate(artifact);
        return (artifact, checksum);
    }

    private static void Validate(LogisticRegressionArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.ModelName) || string.IsNullOrWhiteSpace(artifact.ModelVersion) ||
            string.IsNullOrWhiteSpace(artifact.FeatureSchemaVersion) ||
            string.IsNullOrWhiteSpace(artifact.CalibrationVersion) ||
            string.Equals(artifact.CalibrationVersion, "uncalibrated", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(artifact.OutcomeDefinitionVersion) ||
            string.IsNullOrWhiteSpace(artifact.TrainingDatasetId) || artifact.TrainingDataCutoffUtc == default)
            throw new InvalidDataException("Classification model artifact metadata is incomplete.");
        if (artifact.FeatureSchemaVersion != EvidenceBackedClassificationVersions.FeatureSchemaV1)
            throw new InvalidDataException("Classification model artifact uses an unsupported feature schema.");
        if (!double.IsFinite(artifact.Intercept) || !double.IsFinite(artifact.CalibrationSlope) ||
            !double.IsFinite(artifact.CalibrationIntercept) || artifact.CalibrationSlope <= 0 ||
            artifact.Coefficients.Count == 0 || artifact.Coefficients.Any(item =>
                !LogisticFeatureEncoder.SupportedFeatures.Contains(item.Key) || !double.IsFinite(item.Value)))
            throw new InvalidDataException("Classification model coefficients or calibration parameters are invalid.");
    }
}

public sealed class LogisticRegressionProbabilityScorer(
    LogisticRegressionArtifactProvider artifacts) : IProbabilityScorer
{
    public RawModelPrediction Score(EmailValidationFeatureSnapshot snapshot)
    {
        var (artifact, checksum) = artifacts.Get();
        if (snapshot.FeatureSchemaVersion != artifact.FeatureSchemaVersion)
            throw new InvalidOperationException("Feature schema mismatch.");
        var features = LogisticFeatureEncoder.Encode(snapshot);
        var score = artifact.Intercept + artifact.Coefficients.Sum(item =>
            item.Value * features.GetValueOrDefault(item.Key));
        var metadata = new PredictionModelMetadata(
            artifact.ModelName, artifact.ModelVersion, artifact.FeatureSchemaVersion,
            artifact.CalibrationVersion, artifact.OutcomeDefinitionVersion,
            EvidenceBackedClassificationVersions.DefaultDecisionPolicyV1,
            artifact.TrainingDataCutoffUtc, artifact.TrainingDatasetId, checksum,
            DateTimeOffset.MinValue, ModelRolloutMode.Disabled);
        return new(artifact.Target, score, metadata);
    }
}

public sealed class PlattProbabilityCalibrator(
    LogisticRegressionArtifactProvider artifacts) : IProbabilityCalibrator
{
    public CalibratedPrediction Calibrate(RawModelPrediction prediction)
    {
        var (artifact, _) = artifacts.Get();
        if (prediction.Model.ModelVersion != artifact.ModelVersion)
            throw new InvalidOperationException("Calibrator and model versions do not match.");
        var logit = artifact.CalibrationSlope * prediction.RawScore + artifact.CalibrationIntercept;
        var probability = logit >= 0
            ? 1 / (1 + Math.Exp(-logit))
            : Math.Exp(logit) / (1 + Math.Exp(logit));
        return new(prediction.Target, probability, prediction.Model);
    }
}

public static class LogisticFeatureEncoder
{
    public static IReadOnlySet<string> SupportedFeatures { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "heuristic_evidence_strength", "syntax_valid", "mx_present", "explicit_null_mx", "mx_count",
        "provider_evidence_strength", "catch_all_evidence_strength", "recipient_stage_reached",
        "recipient_accepted", "provider_policy_block", "mailbox_full", "observation_count_log1p",
        "verification_reliability", "target_acceptance_rate", "recipient_rejection_rate",
        "temporary_failure_rate", "rate_limit_rate", "probe_attempted"
    };

    public static IReadOnlyDictionary<string, double> Encode(EmailValidationFeatureSnapshot snapshot) =>
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["heuristic_evidence_strength"] = snapshot.HeuristicEvidenceStrength,
            ["syntax_valid"] = Bool(snapshot.Syntax.SyntaxValid),
            ["mx_present"] = Bool(snapshot.Domain.MxPresent),
            ["explicit_null_mx"] = Bool(snapshot.Domain.ExplicitNullMx),
            ["mx_count"] = snapshot.Domain.MxCount,
            ["provider_evidence_strength"] = snapshot.Domain.ProviderEvidenceStrength,
            ["catch_all_evidence_strength"] = snapshot.Domain.CatchAllEvidenceStrength,
            ["recipient_stage_reached"] = Bool(snapshot.Smtp.RecipientStageReached),
            ["recipient_accepted"] = Bool(snapshot.Smtp.RecipientAccepted),
            ["provider_policy_block"] = Bool(snapshot.Smtp.ProviderPolicyBlock),
            ["mailbox_full"] = Bool(snapshot.Smtp.MailboxFull),
            ["observation_count_log1p"] = Math.Log(1d + snapshot.History.ObservationCount),
            ["verification_reliability"] = snapshot.History.VerificationReliability,
            ["target_acceptance_rate"] = snapshot.History.TargetAcceptanceRate,
            ["recipient_rejection_rate"] = snapshot.History.RecipientRejectionRate,
            ["temporary_failure_rate"] = snapshot.History.TemporaryFailureRate,
            ["rate_limit_rate"] = snapshot.History.RateLimitRate,
            ["probe_attempted"] = Bool(snapshot.Operational.ProbeAttempted)
        };

    private static double Bool(bool value) => value ? 1 : 0;
}

/// <summary>Offline-only, deterministic L2-regularized logistic baseline trainer.</summary>
public sealed class RegularizedLogisticRegressionTrainer
{
    public static LogisticRegressionArtifact Train(
        TrainingDataset dataset,
        PredictionTargetKind target,
        string modelName,
        string modelVersion,
        double l2Regularization = 0.01,
        int iterations = 1_000,
        double learningRate = 0.05,
        int randomSeed = 17)
    {
        if (dataset.Rows.Count == 0 || dataset.Manifest.PositiveCount == 0 || dataset.Manifest.NegativeCount == 0)
            throw new InvalidOperationException("A logistic model requires matured positive and negative rows.");
        if (l2Regularization < 0 || iterations < 1 || learningRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(l2Regularization));
        var names = LogisticFeatureEncoder.SupportedFeatures.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var weights = names.ToDictionary(item => item, _ => 0d, StringComparer.Ordinal);
        var intercept = 0d;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var interceptGradient = 0d;
            var gradients = names.ToDictionary(item => item, _ => 0d, StringComparer.Ordinal);
            foreach (var row in dataset.Rows)
            {
                var features = LogisticFeatureEncoder.Encode(row.Snapshot);
                var logit = intercept + names.Sum(name => weights[name] * features[name]);
                var predicted = logit >= 0 ? 1 / (1 + Math.Exp(-logit)) : Math.Exp(logit) / (1 + Math.Exp(logit));
                var error = predicted - (row.Label == BinaryOutcomeLabel.Positive ? 1 : 0);
                interceptGradient += error;
                foreach (var name in names) gradients[name] += error * features[name];
            }
            var count = dataset.Rows.Count;
            intercept -= learningRate * interceptGradient / count;
            foreach (var name in names)
                weights[name] -= learningRate * (gradients[name] / count + l2Regularization * weights[name]);
        }
        return new LogisticRegressionArtifact
        {
            ModelName = modelName,
            ModelVersion = modelVersion,
            Target = target,
            FeatureSchemaVersion = dataset.Manifest.FeatureSchemaVersion,
            CalibrationVersion = "uncalibrated",
            OutcomeDefinitionVersion = dataset.Manifest.OutcomeDefinitionVersion,
            TrainingDataCutoffUtc = dataset.Manifest.MaturationCutoffUtc,
            TrainingDatasetId = dataset.Manifest.DatasetId,
            Intercept = intercept,
            Coefficients = weights,
            // Calibration must be fitted separately; identity parameters are explicit and
            // the candidate must not be promoted until a held-out calibration fit replaces them.
            CalibrationSlope = 1,
            CalibrationIntercept = 0,
            L2Regularization = l2Regularization,
            RandomSeed = randomSeed
        };
    }
}

/// <summary>Fits Platt scaling on held-out raw scores, never on model-fitting rows.</summary>
public static class PlattCalibrationTrainer
{
    public static LogisticRegressionArtifact Fit(
        LogisticRegressionArtifact uncalibrated,
        IReadOnlyList<(double RawScore, BinaryOutcomeLabel Label)> calibrationRows,
        string calibrationVersion,
        int iterations = 500,
        double learningRate = 0.05)
    {
        if (!string.Equals(uncalibrated.CalibrationVersion, "uncalibrated", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Platt fitting requires an uncalibrated baseline artifact.");
        if (calibrationRows.Count == 0 ||
            !calibrationRows.Any(item => item.Label == BinaryOutcomeLabel.Positive) ||
            !calibrationRows.Any(item => item.Label == BinaryOutcomeLabel.Negative))
            throw new InvalidOperationException("Calibration requires separate positive and negative observations.");
        if (string.IsNullOrWhiteSpace(calibrationVersion) || iterations < 1 || learningRate <= 0)
            throw new ArgumentException("Calibration version and optimization settings are required.");
        var slope = 1d;
        var intercept = 0d;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var slopeGradient = 0d;
            var interceptGradient = 0d;
            foreach (var row in calibrationRows)
            {
                var logit = slope * row.RawScore + intercept;
                var predicted = logit >= 0 ? 1 / (1 + Math.Exp(-logit)) : Math.Exp(logit) / (1 + Math.Exp(logit));
                var error = predicted - (row.Label == BinaryOutcomeLabel.Positive ? 1 : 0);
                slopeGradient += error * row.RawScore;
                interceptGradient += error;
            }
            slope -= learningRate * slopeGradient / calibrationRows.Count;
            intercept -= learningRate * interceptGradient / calibrationRows.Count;
            slope = Math.Max(0.000001, slope);
        }
        return uncalibrated with
        {
            CalibrationVersion = calibrationVersion,
            CalibrationSlope = slope,
            CalibrationIntercept = intercept
        };
    }
}
