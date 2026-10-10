using System.Security.Cryptography;
using System.Text.Json;
using EmailValidation.Core;

namespace EmailValidation.Application;

public sealed record BenchmarkPrediction(string SnapshotId, EmailValidationStatus Status, double? Probability = null);
public sealed record BenchmarkPolicy(string Version, bool ActOnLikely = false, int BootstrapSamples = 500, int Seed = 17);
public sealed record ConfusionMetrics(int Total, int Labeled, int TruthPositive, int TruthNegative,
    int TruePositive, int FalsePositive, int TrueNegative, int FalseNegative, int AbstainedPositive, int AbstainedNegative,
    double? FalsePositiveRate, double? FalseNegativeRate, double? Precision, double? Recall,
    double? FalseValidRate, double? FalseInvalidRate, double? Coverage, double? BrierScore, double? LogLoss);
public sealed record MetricInterval(double? Lower, double? Upper, int DomainCount);
public sealed record BenchmarkComparison(ConfusionMetrics Baseline, ConfusionMetrics Candidate,
    MetricInterval FalsePositiveRateChange, MetricInterval FalseNegativeRateChange, MetricInterval CoverageChange);
public sealed record AccuracyBenchmarkReport(string Version, string DatasetId, string DatasetHash, string InputHash,
    EvidenceCohort Cohort, string FeatureSchemaVersion, string OutcomeDefinitionVersion,
    string CandidateChecksum, BenchmarkPolicy Policy, DateTimeOffset CalibrationStartsUtc, DateTimeOffset TestStartsUtc,
    int TrainingRows, int CalibrationRows, int Unresolved, int Censored, int Excluded,
    BenchmarkComparison OutOfTime, BenchmarkComparison UnseenDomain,
    IReadOnlyDictionary<MailProvider, BenchmarkComparison> Providers,
    IReadOnlyDictionary<string, BenchmarkComparison> RecipientBehaviorSegments)
{
    public string? ModelPolicyHash { get; init; }
    public IReadOnlyList<ValidationPolicyVersions> BaselinePolicyVersions { get; init; } = [];
    public BenchmarkSupplementalMetrics? Supplemental { get; init; }
}

/// <summary>Offline paired evaluation; null means not estimable, never zero observed error.</summary>
public static class AccuracyBenchmark
{
    public static AccuracyBenchmarkReport Evaluate(TrainingDataset dataset,
        IReadOnlyList<BenchmarkPrediction> predictions, BenchmarkPolicy policy,
        DateTimeOffset calibrationStarts, DateTimeOffset testStarts, string candidateChecksum, string inputHash)
    {
        if (string.IsNullOrWhiteSpace(policy.Version) || policy.BootstrapSamples is < 100 or > 10000)
            throw new ArgumentException("A versioned policy and 100–10000 bootstrap samples are required.");
        if (dataset.Manifest.Cohort == EvidenceCohort.Unspecified)
            throw new ArgumentException("Benchmark cohorts must be explicit.");
        var lookup = predictions.ToDictionary(item => item.SnapshotId, StringComparer.Ordinal);
        if (predictions.Any(item => !Enum.IsDefined(item.Status) || item.Probability is { } p && (!double.IsFinite(p) || p is < 0 or > 1)) ||
            dataset.Rows.Any(row => !lookup.ContainsKey(row.SnapshotId)) || lookup.Count != dataset.Rows.Count)
            throw new ArgumentException("Candidate predictions must cover each frozen row exactly once and contain valid probabilities.");
        var split = LeakageSafeDatasetSplitter.Split(dataset.Rows, calibrationStarts, testStarts);
        // Labels learned after a later partition starts must not influence fitting/calibration.
        var train = split.Training.Where(row => (row.LabelMaturedAtUtc ?? row.OutcomeObservedAtUtc) < calibrationStarts).ToArray();
        var calibration = split.Calibration.Where(row => (row.LabelMaturedAtUtc ?? row.OutcomeObservedAtUtc) < testStarts).ToArray();
        var held = split.OutOfTimeTest.Concat(split.UnseenDomainTest).ToArray();
        return new("accuracy-benchmark-v1", dataset.Manifest.DatasetId, dataset.Manifest.DatasetHash, inputHash,
            dataset.Manifest.Cohort, dataset.Manifest.FeatureSchemaVersion, dataset.Manifest.OutcomeDefinitionVersion,
            candidateChecksum, policy, calibrationStarts, testStarts, train.Length, calibration.Length,
            dataset.Manifest.UnresolvedCount, dataset.Manifest.RightCensoredCount, dataset.Manifest.ExcludedCount,
            Compare(split.OutOfTimeTest, lookup, policy), Compare(split.UnseenDomainTest, lookup, policy),
            held.GroupBy(row => row.Snapshot.Domain.Provider).ToDictionary(g => g.Key, g => Compare(g.ToArray(), lookup, policy)),
            held.GroupBy(row => row.Snapshot.Domain.RecipientBehavior.ToString()).ToDictionary(g => g.Key, g => Compare(g.ToArray(), lookup, policy)))
        { BaselinePolicyVersions = dataset.Rows.Select(r => r.Snapshot.PolicyVersions).OfType<ValidationPolicyVersions>().Distinct().ToArray() };
    }

    public static ConfusionMetrics Measure(IReadOnlyList<TrainingDatasetRow> rows,
        IReadOnlyDictionary<string, BenchmarkPrediction>? predictions, bool actOnLikely)
    {
        var tp = 0; var fp = 0; var tn = 0; var fn = 0; var ap = 0; var an = 0;
        var brier = new List<double>(); var logLoss = new List<double>();
        foreach (var row in rows)
        {
            var prediction = predictions?.GetValueOrDefault(row.SnapshotId);
            var status = prediction?.Status ?? row.Snapshot.HeuristicStatus;
            var positive = row.Label == BinaryOutcomeLabel.Positive;
            var accepts = status == EmailValidationStatus.Valid || actOnLikely && status == EmailValidationStatus.LikelyValid;
            var rejects = status == EmailValidationStatus.Invalid || actOnLikely && status == EmailValidationStatus.LikelyInvalid;
            if (accepts) { if (positive) tp++; else fp++; }
            else if (rejects) { if (positive) fn++; else tn++; }
            else { if (positive) ap++; else an++; }
            if (prediction?.Probability is { } probability)
            {
                var y = positive ? 1 : 0;
                brier.Add(Math.Pow(probability - y, 2));
                var p = Math.Clamp(probability, 1e-15, 1-1e-15);
                logLoss.Add(-y * Math.Log(p) - (1-y) * Math.Log(1-p));
            }
        }
        var positives = tp + fn + ap; var negatives = tn + fp + an;
        return new(rows.Count, rows.Count, positives, negatives, tp, fp, tn, fn, ap, an,
            Rate(fp, negatives), Rate(fn, positives), Rate(tp, tp+fp), Rate(tp, positives),
            Rate(fp, tp+fp), Rate(fn, tn+fn), Rate(tp+fp+tn+fn, rows.Count),
            brier.Count == 0 ? null : brier.Average(), logLoss.Count == 0 ? null : logLoss.Average());
    }

    private static BenchmarkComparison Compare(IReadOnlyList<TrainingDatasetRow> rows,
        IReadOnlyDictionary<string, BenchmarkPrediction> predictions, BenchmarkPolicy policy)
    {
        var baseline = Measure(rows, null, policy.ActOnLikely);
        var candidate = Measure(rows, predictions, policy.ActOnLikely);
        var clusters = rows.GroupBy(row => (row.Snapshot.TenantId, row.DomainCorrelationId)).Select(g => g.ToArray()).ToArray();
        var fp = new List<double>(); var fn = new List<double>(); var coverage = new List<double>();
        if (clusters.Length >= 2)
        {
            var random = new Random(policy.Seed);
            for (var iteration = 0; iteration < policy.BootstrapSamples; iteration++)
            {
                var sample = Enumerable.Range(0, clusters.Length).SelectMany(_ => clusters[random.Next(clusters.Length)]).ToArray();
                var b = Measure(sample, null, policy.ActOnLikely); var c = Measure(sample, predictions, policy.ActOnLikely);
                Add(fp, c.FalsePositiveRate - b.FalsePositiveRate); Add(fn, c.FalseNegativeRate - b.FalseNegativeRate);
                Add(coverage, c.Coverage - b.Coverage);
            }
        }
        return new(baseline, candidate, Interval(fp, clusters.Length), Interval(fn, clusters.Length), Interval(coverage, clusters.Length));
    }
    private static void Add(List<double> values, double? value) { if (value.HasValue) values.Add(value.Value); }
    private static double? Rate(int numerator, int denominator) => denominator == 0 ? null : numerator / (double)denominator;
    private static MetricInterval Interval(List<double> values, int domains)
    {
        if (values.Count == 0) return new(null, null, domains);
        values.Sort();
        return new(values[(int)((values.Count-1)*0.025)], values[(int)((values.Count-1)*0.975)], domains);
    }
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
}
