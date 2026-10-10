using System.Text.Json;
using System.Text.Json.Nodes;
using EmailValidation.Application;

namespace EmailValidation.Infrastructure;

public sealed partial class JsonValidationIntelligenceStore
{
    public async Task<ValidationRetentionReport> PruneAsync(ValidationRetentionRequest request,
        CancellationToken cancellationToken = default)
    {
        long changed = 0;
        long oversized = 0;
        foreach (var category in new[] { "mailboxes", "domains", "observations", "recipient-behavior-observations", "outcomes" })
        {
            var directory = Path.Combine(_root, category);
            if (!Directory.Exists(directory)) continue;
            // Enumerate lazily; bound file size and changed records independently of directory size.
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (changed >= request.BatchSize) break;
                cancellationToken.ThrowIfCancellationRequested();
                if (category is "mailboxes" or "domains" && new FileInfo(path).Length > 16 * 1024 * 1024) { oversized++; continue; }
                var gate = FileGate(path);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!File.Exists(path)) continue;
                    if (category is "observations" or "recipient-behavior-observations" or "outcomes")
                    {
                        if (await PruneArrayAsync(path, request, cancellationToken).ConfigureAwait(false)) changed++;
                        continue;
                    }
                    JsonNode? root;
                    await using (var input = File.OpenRead(path))
                        root = await JsonNode.ParseAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var removed = root is JsonArray array
                        ? array.Where(node => Expired(node, request.DetailCutoffUtc)).ToArray()
                        : Expired(root, request.DetailCutoffUtc) ? new[] { root } : [];
                    if (removed.Length == 0) continue;
                    changed++;
                    if (request.DryRun) continue;
                    if (root is JsonArray rows)
                    {
                        foreach (var node in removed) rows.Remove(node);
                        await WriteWithoutGateAsync(path, rows, cancellationToken).ConfigureAwait(false);
                    }
                    else File.Delete(path);
                }
                finally { gate.Release(); }
            }
        }
        if (!request.DryRun)
        {
            _mailboxes.Clear(); _domains.Clear();
            _observations.Clear(); _recipientBehaviorObservations.Clear();
            var count = _outcomes.Count;
            for (var index = 0; index < count && _outcomes.TryDequeue(out var outcome); index++)
                if (outcome.OutcomeObservedAt >= request.DetailCutoffUtc) _outcomes.Enqueue(outcome);
        }
        return new(request.DryRun, new Dictionary<string, long> { ["legacyFiles"] = changed }, 0, oversized);
    }

    private static async Task<bool> PruneArrayAsync(string path, ValidationRetentionRequest request, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var changed = false;
        try
        {
            await using (var input = File.OpenRead(path))
            await using (var output = request.DryRun ? Stream.Null : File.Create(temporary))
            {
                await using var writer = new Utf8JsonWriter(output);
                writer.WriteStartArray();
                var count = 0;
                await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(input,
                    cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (Expired(JsonNode.Parse(element.GetRawText()), request.DetailCutoffUtc)) changed = true;
                    else element.WriteTo(writer);
                    if (++count % 128 == 0) await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                writer.WriteEndArray();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (!request.DryRun && changed) File.Move(temporary, path, true);
            return changed;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Expired(JsonNode? node, DateTimeOffset cutoff)
    {
        if (node is not JsonObject record) return false;
        foreach (var field in new[] { "outcomeObservedAt", "observedAt", "observedAtUtc", "lastValidatedAt", "timestamp" })
            if (record[field] is JsonValue value && value.TryGetValue<string>(out var text) &&
                DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var at)) return at < cutoff;
        return false;
    }
}
