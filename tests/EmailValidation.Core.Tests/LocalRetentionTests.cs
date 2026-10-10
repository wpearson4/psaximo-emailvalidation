using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class LocalRetentionTests
{
    [Fact]
    public async Task LegacyRetention_StreamsOldOutcomeRows_AndPreservesSuppressions()
    {
        var root = Path.Combine(Path.GetTempPath(), "ev-retention-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new EmailValidationOptions { Persistence = new() { StoragePath = root } });
        var store = new JsonValidationIntelligenceStore(options);
        Directory.CreateDirectory(Path.Combine(root, "outcomes"));
        var path = Path.Combine(root, "outcomes", "outcomes.json");
        try
        {
            await File.WriteAllTextAsync(path, "[{\"outcomeObservedAt\":\"2025-01-01T00:00:00Z\"},{\"outcomeObservedAt\":\"2026-10-01T00:00:00Z\"}]");
            await store.AddAsync(new("person@example.test", "Policy", "Test", DateTimeOffset.UtcNow) { TenantId = "tenant-a" });
            var request = new ValidationRetentionRequest(new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                new(2025, 10, 1, 0, 0, 0, TimeSpan.Zero));
            var dry = await store.PruneAsync(request);
            Assert.Equal(1, dry.Records["legacyFiles"]);
            Assert.Contains("2025-01", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            await store.PruneAsync(request with { DryRun = false });
            Assert.DoesNotContain("2025-01", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            Assert.Contains("2026-10", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            Assert.NotNull(await store.GetScopedAsync("person@example.test", "tenant-a"));
            Assert.Null(await store.GetScopedAsync("person@example.test", "tenant-b"));
        }
        finally { Directory.Delete(root, true); }
    }
}
