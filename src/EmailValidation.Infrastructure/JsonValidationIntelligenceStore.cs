using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

/// <summary>
/// Replaceable local persistence for the console/worker host. Records are split by
/// domain, mailbox, observations, outcomes, and suppressions so mailbox evidence
/// cannot accidentally become domain behavior.
/// </summary>
public sealed class JsonValidationIntelligenceStore :
    IValidationIntelligenceStore,
    IValidationObservationStore,
    IDeliveryOutcomeStore,
    IGlobalSuppressionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly PersistenceOptions _options;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, DomainIntelligence> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MailboxIntelligence> _mailboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SuppressionEntry> _suppressions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ValidationObservation>> _observations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileGates = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<DeliveryOutcomeRecord> _outcomes = new();
    private int _outcomesLoaded;

    public JsonValidationIntelligenceStore(IOptions<EmailValidationOptions> options)
    {
        _options = options.Value.Persistence;
        _root = Path.GetFullPath(Path.IsPathRooted(_options.StoragePath)
            ? _options.StoragePath
            : Path.Combine(AppContext.BaseDirectory, _options.StoragePath));
        if (_options.Enabled) Directory.CreateDirectory(_root);
    }

    public async Task<DomainIntelligence?> GetDomainAsync(string domain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_domains.TryGetValue(domain, out var cached)) return cached;
        var loaded = await ReadAsync<DomainIntelligence>(PathFor("domains", domain), cancellationToken).ConfigureAwait(false);
        if (loaded is not null) _domains[domain] = loaded;
        return loaded;
    }

    public async Task<MailboxIntelligence?> GetMailboxAsync(string normalizedEmail, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_mailboxes.TryGetValue(normalizedEmail, out var cached)) return cached;
        var loaded = await ReadAsync<MailboxIntelligence>(PathFor("mailboxes", normalizedEmail), cancellationToken).ConfigureAwait(false);
        if (loaded is not null) _mailboxes[normalizedEmail] = loaded;
        return loaded;
    }

    public async Task SaveDomainAsync(DomainIntelligence intelligence, CancellationToken cancellationToken = default)
    {
        _domains[intelligence.Domain] = intelligence;
        await WriteAsync(PathFor("domains", intelligence.Domain), intelligence, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveMailboxAsync(MailboxIntelligence intelligence, CancellationToken cancellationToken = default)
    {
        _mailboxes[intelligence.NormalizedEmail] = intelligence;
        await WriteAsync(PathFor("mailboxes", intelligence.NormalizedEmail), intelligence, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ValidationObservation>> GetDomainObservationsAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        var queue = await GetObservationQueueAsync(domain, cancellationToken).ConfigureAwait(false);
        return queue.ToArray();
    }

    public async Task RecordAsync(ValidationObservation observation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queue = await GetObservationQueueAsync(observation.Domain, cancellationToken).ConfigureAwait(false);
        queue.Enqueue(observation);
        var maximum = Math.Max(1, _options.MaximumObservationsPerDomain);
        while (queue.Count > maximum) queue.TryDequeue(out _);
        if (!_options.Enabled) return;

        var path = PathFor("observations", observation.Domain);
        var gate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteWithoutGateAsync(path, queue.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<ConcurrentQueue<ValidationObservation>> GetObservationQueueAsync(
        string domain,
        CancellationToken cancellationToken)
    {
        if (_observations.TryGetValue(domain, out var cached)) return cached;
        var path = PathFor("observations", domain);
        var gate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_observations.TryGetValue(domain, out cached)) return cached;
            var loaded = _options.Enabled
                ? await ReadWithoutGateAsync<List<ValidationObservation>>(path, cancellationToken).ConfigureAwait(false) ?? []
                : [];
            cached = new ConcurrentQueue<ValidationObservation>(loaded);
            _observations[domain] = cached;
            return cached;
        }
        finally { gate.Release(); }
    }

    public async Task RecordOutcomeAsync(DeliveryOutcome outcome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Retain compatibility for old callers without fabricating a mailbox-level
        // prediction snapshot. Such records are deliberately not calibration samples.
        if (!_options.Enabled) return;
        var path = Path.Combine(_root, "outcomes", "legacy-domain-outcomes.json");
        var gate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadWithoutGateAsync<List<DeliveryOutcome>>(path, cancellationToken).ConfigureAwait(false) ?? [];
            records.Add(outcome);
            await WriteWithoutGateAsync(path, records, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task RecordAsync(DeliveryOutcomeRecord outcome, CancellationToken cancellationToken = default)
    {
        await EnsureOutcomesLoadedAsync(cancellationToken).ConfigureAwait(false);
        outcome = outcome with
        {
            Prediction = outcome.Prediction with { ReasonCodes = outcome.Prediction.ReasonCodes.ToArray() }
        };
        _outcomes.Enqueue(outcome);
        await WriteOutcomesAsync(cancellationToken).ConfigureAwait(false);
        if (outcome.ActualOutcome == DeliveryOutcomeKind.HardBounce)
        {
            await AddAsync(new SuppressionEntry(
                outcome.Prediction.NormalizedEmail,
                "HistoricalHardBounce",
                outcome.Source ?? "DeliveryOutcome",
                outcome.OutcomeObservedAt), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DeliveryOutcomeRecord>> QueryAsync(
        CalibrationQuery query,
        CancellationToken cancellationToken = default)
    {
        await EnsureOutcomesLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _outcomes.Where(item => Matches(item, query)).ToArray();
    }

    public async Task<SuppressionEntry?> GetAsync(string normalizedEmail, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_suppressions.TryGetValue(normalizedEmail, out var cached)) return cached;
        var loaded = await ReadAsync<SuppressionEntry>(PathFor("suppressions", normalizedEmail), cancellationToken).ConfigureAwait(false);
        if (loaded is not null) _suppressions[normalizedEmail] = loaded;
        return loaded;
    }

    public async Task AddAsync(SuppressionEntry entry, CancellationToken cancellationToken = default)
    {
        _suppressions[entry.NormalizedEmail] = entry;
        await WriteAsync(PathFor("suppressions", entry.NormalizedEmail), entry, cancellationToken).ConfigureAwait(false);
    }

    private static bool Matches(DeliveryOutcomeRecord item, CalibrationQuery query)
    {
        var prediction = item.Prediction;
        return (!query.Provider.HasValue || prediction.Provider == query.Provider) &&
            (!query.Status.HasValue || prediction.PredictedStatus == query.Status) &&
            (!query.MinimumConfidence.HasValue || prediction.PredictedConfidence >= query.MinimumConfidence) &&
            (!query.MaximumConfidence.HasValue || prediction.PredictedConfidence <= query.MaximumConfidence) &&
            (!query.CatchAllStatus.HasValue || prediction.CatchAllStatus == query.CatchAllStatus) &&
            (!query.VerificationReliability.HasValue || prediction.VerificationReliability == query.VerificationReliability) &&
            (!query.ReasonCode.HasValue || prediction.ReasonCodes.Contains(query.ReasonCode.Value)) &&
            (query.DomainType is null || string.Equals(prediction.DomainType, query.DomainType, StringComparison.OrdinalIgnoreCase)) &&
            (query.ClassificationPolicyVersion is null || string.Equals(
                prediction.Policy.ClassificationPolicyVersion, query.ClassificationPolicyVersion, StringComparison.Ordinal)) &&
            (query.ProviderStrategyVersion is null || string.Equals(
                prediction.Policy.ProviderStrategyVersion, query.ProviderStrategyVersion, StringComparison.Ordinal)) &&
            (!query.MaximumEvidenceAgeHours.HasValue || prediction.EvidenceAgeHours <= query.MaximumEvidenceAgeHours);
    }

    private async Task EnsureOutcomesLoadedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _outcomesLoaded) == 1) return;
        if (!_options.Enabled)
        {
            Volatile.Write(ref _outcomesLoaded, 1);
            return;
        }
        var path = Path.Combine(_root, "outcomes", "outcomes.json");
        var gate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_outcomesLoaded == 1) return;
            var loaded = await ReadWithoutGateAsync<List<DeliveryOutcomeRecord>>(path, cancellationToken).ConfigureAwait(false) ?? [];
            foreach (var item in loaded) _outcomes.Enqueue(item);
            Volatile.Write(ref _outcomesLoaded, 1);
        }
        finally { gate.Release(); }
    }

    private async Task WriteOutcomesAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "outcomes", "outcomes.json");
        if (!_options.Enabled) return;
        var gate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteWithoutGateAsync(path, _outcomes.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private string PathFor(string category, string key) =>
        Path.Combine(_root, category, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant()))) + ".json");

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !File.Exists(path)) return default;
        var gate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadWithoutGateAsync<T>(path, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        var gate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteWithoutGateAsync(path, value, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private static async Task<T?> ReadWithoutGateAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteWithoutGateAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed class PersistentDomainValidationCache : IDomainValidationCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IValidationIntelligenceStore _store;
    private readonly TimeSpan? _configuredMemoryLifetime;

    public PersistentDomainValidationCache(
        IValidationIntelligenceStore store,
        IOptions<EmailValidationOptions>? options = null)
    {
        _store = store;
        _configuredMemoryLifetime = options is null
            ? null
            : TimeSpan.FromMinutes(Math.Max(0, options.Value.DomainIntelligence.MemoryCacheMinutes));
    }

    public int Count => _cache.Count;

    public bool TryGet(string domain, out DomainIntelligence? data)
    {
        if (_cache.TryGetValue(domain, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            data = entry.Value;
            return true;
        }
        _cache.TryRemove(domain, out _);
        data = null;
        return false;
    }

    public void Store(DomainIntelligence data, TimeSpan lifetime) =>
        _cache[data.Domain] = new(data, DateTimeOffset.UtcNow.Add(lifetime));

    public async Task<DomainIntelligence?> GetAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (TryGet(domain, out var cached)) return cached;
        var stored = await _store.GetDomainAsync(domain, cancellationToken).ConfigureAwait(false);
        if (stored is null) return null;
        if (stored.EvidenceExpiresAt is { } expiresAt && expiresAt > DateTimeOffset.UtcNow)
            _cache[domain] = new(stored, MemoryExpiration(expiresAt));
        // Return stale durable evidence to the planner as historical context. The
        // planner must refresh it before allowing it to suppress live SMTP work.
        return stored;
    }

    public async Task StoreAsync(DomainIntelligence data, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var durable = data with { EvidenceExpiresAt = DateTimeOffset.UtcNow.Add(lifetime) };
        Store(durable, MemoryLifetime(lifetime));
        await _store.SaveDomainAsync(durable, cancellationToken).ConfigureAwait(false);
    }

    private TimeSpan MemoryLifetime(TimeSpan durableLifetime) => _configuredMemoryLifetime is null
        ? durableLifetime
        : _configuredMemoryLifetime.Value <= durableLifetime
            ? _configuredMemoryLifetime.Value
            : durableLifetime;

    private DateTimeOffset MemoryExpiration(DateTimeOffset durableExpiration)
    {
        var configured = _configuredMemoryLifetime;
        if (configured is null) return durableExpiration;
        var memoryExpiration = DateTimeOffset.UtcNow.Add(configured.Value);
        return memoryExpiration <= durableExpiration ? memoryExpiration : durableExpiration;
    }

    private sealed record CacheEntry(DomainIntelligence Value, DateTimeOffset ExpiresAt);
}
