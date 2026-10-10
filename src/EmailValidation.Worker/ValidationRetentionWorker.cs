using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Worker;

public sealed class ValidationRetentionWorker(IValidationRetentionStore retention,
    IOptions<EmailValidationOptions> options, TimeProvider clock,
    ILogger<ValidationRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Retention.Enabled || !settings.Persistence.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.Retention.SweepIntervalMinutes), clock);
        do
        {
            try
            {
                var now = clock.GetUtcNow();
                var report = await retention.SweepAsync(new(now.AddDays(-settings.Retention.DetailDays),
                    now.AddDays(-settings.Retention.BenchmarkDays), settings.Retention.BatchSize, DryRun: false), stoppingToken)
                    .ConfigureAwait(false);
                logger.LogInformation("Validation retention deleted {Count} records; protected {ActiveCount} active records; skipped {SkippedCount} oversized legacy documents",
                    report.Records.Values.Sum(), report.ProtectedActiveRecords, report.SkippedRecords);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError("Validation retention sweep failed ({ErrorType}); a later sweep will resume", exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
