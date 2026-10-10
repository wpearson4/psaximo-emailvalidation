namespace EmailValidation.Application;

/// <summary>Explicit cutoffs keep retention separate from evidence freshness and cache expiry.</summary>
public sealed record ValidationRetentionRequest(DateTimeOffset DetailCutoffUtc,
    DateTimeOffset BenchmarkCutoffUtc, int BatchSize = 200, bool DryRun = true);

public sealed record ValidationRetentionReport(bool DryRun, IReadOnlyDictionary<string, long> Records,
    long ProtectedActiveRecords, long SkippedRecords = 0);

public interface IValidationRetentionStore
{
    Task<ValidationRetentionReport> SweepAsync(ValidationRetentionRequest request,
        CancellationToken cancellationToken = default);
}
