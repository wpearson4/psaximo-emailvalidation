using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

/// <summary>Coordinates the configured persistence adapter and historical local copies.</summary>
public sealed class ValidationRetentionStore(JsonValidationIntelligenceStore legacy, LocalClassificationEvidenceStore localEvidence,
    IOptions<EmailValidationOptions> options, IElasticsearchObservationSink projection, MongoValidationRetentionStore? durable = null) : IValidationRetentionStore
{
    public async Task<ValidationRetentionReport> SweepAsync(ValidationRetentionRequest request,
        CancellationToken cancellationToken = default)
    {
        var reports = new List<ValidationRetentionReport>();
        if (options.Value.Persistence.Provider.Equals("MongoDB", StringComparison.OrdinalIgnoreCase))
            reports.Add(await (durable ?? throw new InvalidOperationException("Mongo retention is unavailable."))
                .SweepAsync(request, cancellationToken).ConfigureAwait(false));
        reports.Add(await legacy.PruneAsync(request, cancellationToken).ConfigureAwait(false));
        if (options.Value.Persistence.Provider.Equals("Json", StringComparison.OrdinalIgnoreCase))
            reports.Add(await localEvidence.PruneAsync(request, cancellationToken).ConfigureAwait(false));
        if (options.Value.Projection.Enabled)
            reports.Add(new(request.DryRun, new Dictionary<string, long> { ["projectionDocuments"] =
                await projection.PruneAsync(request, cancellationToken).ConfigureAwait(false) }, 0));
        return new(request.DryRun, reports.SelectMany(report => report.Records).ToDictionary(pair => pair.Key, pair => pair.Value),
            reports.Sum(report => report.ProtectedActiveRecords), reports.Sum(report => report.SkippedRecords));
    }
}
