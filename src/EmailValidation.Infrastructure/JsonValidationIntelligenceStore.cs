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
public sealed partial class JsonValidationIntelligenceStore :
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
    private readonly ValidationRetentionOptions _retention;
    private readonly CatchAllOptions _catchAllOptions;
    private readonly string _root;
    private readonly BoundedEvidenceCache<DomainIntelligence> _domains;
    private readonly TimeProvider _clock;
    private readonly BoundedEvidenceCache<MailboxIntelligence> _mailboxes;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ValidationObservation>> _observations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ValidationObservation>> _recipientBehaviorObservations =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim[] FileGates = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static SemaphoreSlim FileGate(string path) => FileGates[(int)((uint)StringComparer.Ordinal.GetHashCode(path) % (uint)FileGates.Length)];
    private readonly ConcurrentQueue<DeliveryOutcomeRecord> _outcomes = new();
    private int _outcomesLoaded;

    public JsonValidationIntelligenceStore(IOptions<EmailValidationOptions> options, TimeProvider? timeProvider = null)
    {
        _options = options.Value.Persistence;
        _retention = options.Value.Retention;
        _clock = timeProvider ?? TimeProvider.System;
        _domains = new(_options.EvidenceCacheSizeLimit, _clock);
        _mailboxes = new(_options.EvidenceCacheSizeLimit, _clock);
        _catchAllOptions = options.Value.CatchAll;
        _root = Path.GetFullPath(Path.IsPathRooted(_options.StoragePath)
            ? _options.StoragePath
            : Path.Combine(AppContext.BaseDirectory, _options.StoragePath));
        if (_options.Enabled) Directory.CreateDirectory(_root);
    }

    public async Task<DomainIntelligence?> GetDomainAsync(string domain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_domains.TryGet(domain.ToLowerInvariant(), out var cached)) return cached;
        var loaded = await ReadAsync<DomainIntelligence>(PathFor("domains", domain), cancellationToken).ConfigureAwait(false);
        if (loaded is null) return null;
        loaded = loaded with
        {
            CatchAll = DomainRecipientBehaviorPolicy.NormalizePersisted(
                loaded.CatchAll,
                _catchAllOptions.AcceptAllMinimumIndependentObservations,
                _catchAllOptions.MinimumAcceptedProbes)
        };
        _domains.Set(domain.ToLowerInvariant(), loaded, _clock.GetUtcNow().AddSeconds(_options.EvidenceCacheSeconds));
        return loaded;
    }

    public async Task<MailboxIntelligence?> GetMailboxAsync(string normalizedEmail, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = MailboxIdentity.Create(normalizedEmail).Key;
        if (_mailboxes.TryGet(key, out var cached)) return cached;
        var loaded = await ReadAsync<MailboxIntelligence>(PathFor("mailboxes", normalizedEmail), cancellationToken).ConfigureAwait(false);
        if (loaded?.MailboxKey != key || !MailboxIdentity.Matches(key, loaded.NormalizedEmail) ||
            !MailboxIdentity.Matches(loaded.LastResult.MailboxKey, loaded.LastResult.NormalizedEmail) ||
            loaded.LastResult.MailboxKey != key) return null;
        _mailboxes.Set(key, loaded, _clock.GetUtcNow().AddSeconds(_options.EvidenceCacheSeconds));
        return loaded;
    }

    public async Task SaveDomainAsync(DomainIntelligence intelligence, CancellationToken cancellationToken = default) =>
        _ = await MergeDomainAsync(intelligence, cancellationToken).ConfigureAwait(false);

    public async Task<DomainIntelligence> MergeDomainAsync(DomainIntelligence intelligence, CancellationToken cancellationToken = default)
    {
        var path = PathFor("domains", intelligence.Domain);
        var gate = FileGate(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _domains.TryGet(intelligence.Domain.ToLowerInvariant(), out var current);
            if (_options.Enabled) current = await ReadWithoutGateAsync<DomainIntelligence>(path, cancellationToken).ConfigureAwait(false);
            var merged = DomainIntelligenceMerge.Merge(current, intelligence);
            merged = merged with { CatchAll = DomainRecipientBehaviorPolicy.NormalizePersisted(merged.CatchAll,
                _catchAllOptions.AcceptAllMinimumIndependentObservations, _catchAllOptions.MinimumAcceptedProbes) };
            if (_options.Enabled) await WriteWithoutGateAsync(path, merged, cancellationToken).ConfigureAwait(false);
            _domains.Set(merged.Domain.ToLowerInvariant(), merged, _clock.GetUtcNow().AddSeconds(_options.EvidenceCacheSeconds));
            return merged;
        }
        finally { gate.Release(); }
    }

    public async Task SaveMailboxAsync(MailboxIntelligence intelligence, CancellationToken cancellationToken = default)
    {
        if (!MailboxIdentity.Matches(intelligence.MailboxKey, intelligence.NormalizedEmail) ||
            intelligence.LastResult.MailboxKey != intelligence.MailboxKey ||
            !MailboxIdentity.Matches(intelligence.LastResult.MailboxKey, intelligence.LastResult.NormalizedEmail)) return;
        _mailboxes.Set(intelligence.MailboxKey!, intelligence, _clock.GetUtcNow().AddSeconds(_options.EvidenceCacheSeconds));
        await WriteAsync(PathFor("mailboxes", intelligence.NormalizedEmail), intelligence, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ValidationObservation>> GetDomainObservationsAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        var general = await GetObservationQueueAsync(domain, cancellationToken).ConfigureAwait(false);
        var recipientBehavior = await GetRecipientBehaviorObservationQueueAsync(domain, cancellationToken)
            .ConfigureAwait(false);
        return general.Concat(recipientBehavior)
            .OrderBy(observation => observation.ObservedAt)
            .ToArray();
    }

    public async Task RecordAsync(ValidationObservation observation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var behaviorObservation =
            DomainRecipientBehaviorPolicy.RequiresProtectedObservationRetention(observation);
        var queue = behaviorObservation
            ? await GetRecipientBehaviorObservationQueueAsync(observation.Domain, cancellationToken).ConfigureAwait(false)
            : await GetObservationQueueAsync(observation.Domain, cancellationToken).ConfigureAwait(false);
        queue.Enqueue(observation);
        var maximum = Math.Max(1, _options.MaximumObservationsPerDomain);
        while (queue.Count > maximum) queue.TryDequeue(out _);
        if (!_options.Enabled) return;

        var path = PathFor(
            behaviorObservation ? "recipient-behavior-observations" : "observations",
            observation.Domain);
        var gate = FileGate(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteWithoutGateAsync(path, queue.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<ConcurrentQueue<ValidationObservation>> GetObservationQueueAsync(
        string domain,
        CancellationToken cancellationToken) => await GetObservationQueueAsync(
            domain,
            "observations",
            _observations,
            cancellationToken).ConfigureAwait(false);

    private async Task<ConcurrentQueue<ValidationObservation>> GetRecipientBehaviorObservationQueueAsync(
        string domain,
        CancellationToken cancellationToken) => await GetObservationQueueAsync(
            domain,
            "recipient-behavior-observations",
            _recipientBehaviorObservations,
            cancellationToken).ConfigureAwait(false);

    private async Task<ConcurrentQueue<ValidationObservation>> GetObservationQueueAsync(
        string domain,
        string category,
        ConcurrentDictionary<string, ConcurrentQueue<ValidationObservation>> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(domain, out var cached)) return cached;
        var path = PathFor(category, domain);
        var gate = FileGate(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(domain, out cached)) return cached;
            var loaded = _options.Enabled
                ? await ReadWithoutGateAsync<List<ValidationObservation>>(path, cancellationToken).ConfigureAwait(false) ?? []
                : [];
            cached = new ConcurrentQueue<ValidationObservation>(loaded);
            cache[domain] = cached;
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
        var gate = FileGate(path);
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
        if (outcome.ActualOutcome == DeliveryOutcomeKind.HardBounce &&
            MailboxIdentity.Matches(outcome.Prediction.MailboxKey, outcome.Prediction.NormalizedEmail))
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
        return _outcomes.Where(item => (!_retention.Enabled || item.OutcomeObservedAt >= _clock.GetUtcNow().AddDays(-_retention.DetailDays)) && Matches(item, query)).ToArray();
    }

    public Task<SuppressionEntry?> GetAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        GetScopedAsync(normalizedEmail, null, cancellationToken);

    public async Task<SuppressionEntry?> GetScopedAsync(string normalizedEmail, string? tenantId,
        CancellationToken cancellationToken = default)
    {
        var identity = MailboxIdentity.Create(normalizedEmail);
        foreach (var scope in string.IsNullOrWhiteSpace(tenantId) ? new string?[] { null } : [tenantId, null])
        {
            var loaded = await ReadAsync<SuppressionEntry>(SuppressionPath(identity.Address, scope), cancellationToken).ConfigureAwait(false);
            if (loaded?.MailboxKey == identity.Key && loaded.TenantId == scope &&
                MailboxIdentity.Matches(identity.Key, loaded.NormalizedEmail)) return loaded;
        }
        return null;
    }

    public async Task AddAsync(SuppressionEntry entry, CancellationToken cancellationToken = default)
    {
        var identity = MailboxIdentity.Create(entry.NormalizedEmail);
        entry = entry with { NormalizedEmail = identity.Address, MailboxKey = identity.Key };
        await WriteAsync(SuppressionPath(entry.NormalizedEmail, entry.TenantId), entry, cancellationToken).ConfigureAwait(false);
    }

    private string SuppressionPath(string email, string? tenantId) => tenantId is null
        ? PathFor("suppressions", email)
        : Path.Combine(_root, "tenant-suppressions", Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[] { tenantId, MailboxIdentity.Create(email).Key })))) + ".json");

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
        var gate = FileGate(path);
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
        var gate = FileGate(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteWithoutGateAsync(path, _outcomes.Where(item => !_retention.Enabled || item.OutcomeObservedAt >= _clock.GetUtcNow().AddDays(-_retention.DetailDays)).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private string PathFor(string category, string key)
    {
        var identity = category is "mailboxes" or "suppressions"
            ? MailboxIdentity.Create(key).Key
            : key.ToLowerInvariant();
        return Path.Combine(_root, category, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !File.Exists(path)) return default;
        var gate = FileGate(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadWithoutGateAsync<T>(path, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        var gate = FileGate(path);
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
    private readonly BoundedEvidenceCache<DomainIntelligence> _cache;
    private readonly IValidationIntelligenceStore _store;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _memoryLifetime;
    private readonly object _sync = new();
    public PersistentDomainValidationCache(IValidationIntelligenceStore store,
        IOptions<EmailValidationOptions>? options = null, TimeProvider? timeProvider = null)
    {
        _store = store;
        _clock = timeProvider ?? TimeProvider.System;
        var settings = options?.Value ?? new EmailValidationOptions();
        _cache = new(settings.Persistence.EvidenceCacheSizeLimit, _clock);
        _memoryLifetime = TimeSpan.FromSeconds(Math.Min(settings.Persistence.EvidenceCacheSeconds,
            settings.DomainIntelligence.MemoryCacheMinutes * 60));
    }
    public int Count => _cache.Count;
    public bool TryGet(string domain, out DomainIntelligence? data) => _cache.TryGet(domain.ToLowerInvariant(), out data);
    public void Store(DomainIntelligence data, TimeSpan lifetime)
    {
        lock (_sync)
        {
            var key = data.Domain.ToLowerInvariant();
            if (_cache.TryGet(key, out var current) && current!.ProfileVersion > data.ProfileVersion) return;
            var expires = _clock.GetUtcNow().Add(lifetime < _memoryLifetime ? lifetime : _memoryLifetime);
            if (data.EvidenceExpiresAt is { } observed && observed < expires) expires = observed;
            _cache.Set(key, data, expires);
        }
    }
    public async Task<DomainIntelligence?> GetAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (TryGet(domain, out var cached)) return cached;
        var stored = await _store.GetDomainAsync(domain, cancellationToken).ConfigureAwait(false);
        if (stored is not null) Store(stored, _memoryLifetime);
        if (stored is not null && TryGet(domain, out var latest) && latest!.ProfileVersion > stored.ProfileVersion) return latest;
        return stored;
    }
    public async Task StoreAsync(DomainIntelligence data, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        _ = await StoreMergedAsync(data, lifetime, cancellationToken).ConfigureAwait(false);
    public async Task<DomainIntelligence> StoreMergedAsync(DomainIntelligence data, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var durable = data with { EvidenceExpiresAt = data.EvidenceExpiresAt ?? data.ObservedAt.Add(lifetime) };
        var stored = await _store.MergeDomainAsync(durable, cancellationToken).ConfigureAwait(false);
        Store(stored, lifetime);
        return TryGet(stored.Domain, out var latest) && latest!.ProfileVersion > stored.ProfileVersion ? latest : stored;
    }
}
