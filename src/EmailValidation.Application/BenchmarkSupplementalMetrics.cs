using EmailValidation.Core;
namespace EmailValidation.Application;

public sealed record BenchmarkMeasurement(string SnapshotId, double? LatencyMilliseconds = null,
    bool? RetryScheduled = null, double? AllocatedCostUsd = null, DomainRecipientBehavior? RecipientBehaviorTruth = null);
public sealed record BenchmarkSupplementalMetrics(int TotalRequests, double? BaselineInconclusiveRate,
    int LatencyObservations, double? MeanLatencyMilliseconds, double? P50LatencyMilliseconds, double? P95LatencyMilliseconds,
    int RetryObservations, double? RetryFrequency, int CostObservations, double? MeanAllocatedCostUsd,
    int RecipientBehaviorTruthCount, double? RecipientBehaviorAccuracy,
    IReadOnlyDictionary<string, int> RecipientBehaviorConfusion)
{
    public static BenchmarkSupplementalMetrics Calculate(IReadOnlyList<EmailValidationFeatureSnapshot> snapshots,
        IReadOnlyList<BenchmarkMeasurement> measurements)
    {
        var lookup = snapshots.ToDictionary(s => s.SnapshotId);
        if (measurements.Select(m => m.SnapshotId).Distinct().Count() != measurements.Count ||
            measurements.Any(m => !lookup.ContainsKey(m.SnapshotId) || Invalid(m.LatencyMilliseconds) || Invalid(m.AllocatedCostUsd) ||
                m.RecipientBehaviorTruth.HasValue && !Enum.IsDefined(m.RecipientBehaviorTruth.Value)))
            throw new ArgumentException("Measurements require unique known snapshots and finite nonnegative values.");
        var latency = measurements.Where(m => m.LatencyMilliseconds.HasValue).Select(m => m.LatencyMilliseconds!.Value).Order().ToArray();
        var retries = measurements.Where(m => m.RetryScheduled.HasValue).ToArray();
        var costs = measurements.Where(m => m.AllocatedCostUsd.HasValue).Select(m => m.AllocatedCostUsd!.Value).ToArray();
        var routing = measurements.Where(m => m.RecipientBehaviorTruth.HasValue).ToArray();
        return new(snapshots.Count, snapshots.Count == 0 ? null : snapshots.Count(s => s.HeuristicStatus == EmailValidationStatus.Unknown)/(double)snapshots.Count,
            latency.Length, Mean(latency), Quantile(latency,0.5), Quantile(latency,0.95),
            retries.Length, retries.Length == 0 ? null : retries.Count(m => m.RetryScheduled == true)/(double)retries.Length,
            costs.Length, Mean(costs), routing.Length,
            routing.Length == 0 ? null : routing.Count(m => m.RecipientBehaviorTruth == lookup[m.SnapshotId].Domain.RecipientBehavior)/(double)routing.Length,
            routing.GroupBy(m => $"truth:{m.RecipientBehaviorTruth};prediction:{lookup[m.SnapshotId].Domain.RecipientBehavior}")
                .ToDictionary(g => g.Key,g => g.Count()));
    }
    private static bool Invalid(double? value) => value.HasValue && (!double.IsFinite(value.Value) || value.Value < 0);
    private static double? Mean(double[] values) => values.Length == 0 ? null : values.Average();
    private static double? Quantile(double[] values,double fraction) => values.Length == 0 ? null : values[(int)Math.Ceiling((values.Length-1)*fraction)];
}
