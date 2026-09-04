using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public sealed class ClassificationFoundationMetrics : IClassificationFoundationMetrics, IDisposable
{
    private readonly Meter _meter = new("EmailValidation.Classification", "1.0.0");
    private readonly Counter<long> _outcomes;
    private readonly Counter<long> _snapshots;
    private readonly Counter<long> _scored;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _abstained;
    private readonly Counter<long> _disagreements;
    private readonly Counter<long> _datasetRows;
    private readonly Histogram<double> _latency;

    public ClassificationFoundationMetrics()
    {
        _outcomes = _meter.CreateCounter<long>("outcome_ingested_total");
        _snapshots = _meter.CreateCounter<long>("feature_snapshot_created_total");
        _scored = _meter.CreateCounter<long>("classification_model_scored_total");
        _failures = _meter.CreateCounter<long>("classification_model_failure_total");
        _abstained = _meter.CreateCounter<long>("classification_model_abstained_total");
        _disagreements = _meter.CreateCounter<long>("classification_model_shadow_disagreement_total");
        _datasetRows = _meter.CreateCounter<long>("training_dataset_rows");
        _latency = _meter.CreateHistogram<double>("classification_model_latency", "ms");
    }

    public void RecordOutcome(AppendObservationResult result, EmailDeliveryOutcome outcome) =>
        _outcomes.Add(1, new TagList { { "result", result.ToString() }, { "outcome", outcome.ToString() } });

    public void RecordSnapshot(bool created) =>
        _snapshots.Add(1, new TagList { { "result", created ? "created" : "not_created" } });

    public void RecordModelScored(
        ModelRolloutMode mode,
        bool succeeded,
        bool abstained,
        bool disagreed,
        TimeSpan elapsed)
    {
        var tags = new TagList { { "mode", mode.ToString() } };
        if (succeeded) _scored.Add(1, tags); else _failures.Add(1, tags);
        if (abstained) _abstained.Add(1, tags);
        if (disagreed) _disagreements.Add(1, tags);
        _latency.Record(elapsed.TotalMilliseconds, tags);
    }

    public void RecordDataset(TrainingDatasetManifest manifest) =>
        _datasetRows.Add(manifest.TrainingRowCount,
            new TagList { { "target", manifest.OutcomeDefinitionVersion }, { "schema", manifest.FeatureSchemaVersion } });

    public void Dispose() => _meter.Dispose();
}

public sealed class LocalClassificationEvidenceStore :
    IEmailDeliveryOutcomeObservationStore,
    IEmailValidationFeatureSnapshotStore,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PersistenceOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, EmailDeliveryOutcomeObservation> _outcomes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EmailValidationFeatureSnapshot> _snapshots = new(StringComparer.Ordinal);
    private int _loaded;

    public LocalClassificationEvidenceStore(IOptions<EmailValidationOptions> options) => _options = options.Value.Persistence;

    public async Task<AppendObservationResult> AppendAsync(
        EmailDeliveryOutcomeObservation observation,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_outcomes.TryGetValue(observation.OutcomeEventId, out var existing))
            return existing == observation ? AppendObservationResult.Duplicate : AppendObservationResult.Conflict;
        var natural = _outcomes.Values.FirstOrDefault(item => SameNaturalEvent(item, observation));
        if (natural is not null && natural.Outcome == observation.Outcome)
            return AppendObservationResult.Duplicate;
        var conflict = natural is not null;
        if (!_outcomes.TryAdd(observation.OutcomeEventId, observation))
            return AppendObservationResult.Duplicate;
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return conflict ? AppendObservationResult.Conflict : AppendObservationResult.Inserted;
    }

    async Task<bool> IEmailValidationFeatureSnapshotStore.AppendAsync(
        EmailValidationFeatureSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (!_snapshots.TryAdd(snapshot.SnapshotId, snapshot)) return false;
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<EmailDeliveryOutcomeObservation>> QueryAsync(
        DateTimeOffset observedFromUtc,
        DateTimeOffset observedThroughUtc,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _outcomes.Values.Where(item => item.ObservedAtUtc >= observedFromUtc &&
            item.ObservedAtUtc <= observedThroughUtc &&
            (tenantId is null || string.Equals(item.TenantId, tenantId, StringComparison.Ordinal))).ToArray();
    }

    public async Task<IReadOnlyList<EmailValidationFeatureSnapshot>> QueryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        string featureSchemaVersion,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _snapshots.Values.Where(item => item.SnapshotAtUtc >= fromUtc && item.SnapshotAtUtc <= throughUtc &&
            item.FeatureSchemaVersion == featureSchemaVersion &&
            (tenantId is null || string.Equals(item.TenantId, tenantId, StringComparison.Ordinal))).ToArray();
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _loaded) == 1) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded == 1) return;
            if (IsPersistent && File.Exists(FilePath))
            {
                await using var stream = File.OpenRead(FilePath);
                var data = await JsonSerializer.DeserializeAsync<LocalData>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var item in data?.Outcomes ?? []) _outcomes[item.OutcomeEventId] = item;
                foreach (var item in data?.Snapshots ?? []) _snapshots[item.SnapshotId] = item;
            }
            Volatile.Write(ref _loaded, 1);
        }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (!IsPersistent) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = File.Create(temporary))
                {
                    await JsonSerializer.SerializeAsync(stream,
                        new LocalData(_outcomes.Values.ToArray(), _snapshots.Values.ToArray()),
                        JsonOptions, cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporary, FilePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    private bool IsPersistent => _options.Enabled &&
        string.Equals(_options.Provider, "Json", StringComparison.OrdinalIgnoreCase);
    private string FilePath => Path.Combine(Path.GetFullPath(Path.IsPathRooted(_options.StoragePath)
        ? _options.StoragePath : Path.Combine(AppContext.BaseDirectory, _options.StoragePath)),
        "classification", "evidence.json");

    private static bool SameNaturalEvent(EmailDeliveryOutcomeObservation left, EmailDeliveryOutcomeObservation right) =>
        left.EmailCorrelationId == right.EmailCorrelationId && left.OutcomeSource == right.OutcomeSource &&
        left.SendAttemptAtUtc == right.SendAttemptAtUtc && left.ObservedAtUtc == right.ObservedAtUtc;

    private sealed record LocalData(
        IReadOnlyList<EmailDeliveryOutcomeObservation> Outcomes,
        IReadOnlyList<EmailValidationFeatureSnapshot> Snapshots);

    public void Dispose() => _gate.Dispose();
}

public sealed class MongoClassificationEvidenceStore :
    IEmailDeliveryOutcomeObservationStore,
    IEmailValidationFeatureSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IMongoCollection<OutcomeDocument> _outcomes;
    private readonly IMongoCollection<SnapshotDocument> _snapshots;

    public MongoClassificationEvidenceStore(IMongoClient client, IOptions<EmailValidationOptions> options)
    {
        var persistence = options.Value.Persistence;
        var database = client.GetDatabase(persistence.DatabaseName);
        _outcomes = database.GetCollection<OutcomeDocument>(persistence.OutcomeObservationCollection);
        _snapshots = database.GetCollection<SnapshotDocument>(persistence.FeatureSnapshotCollection);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _outcomes.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<OutcomeDocument>(
                Builders<OutcomeDocument>.IndexKeys.Ascending(item => item.EmailCorrelationId)
                    .Ascending(item => item.ObservedAtUtc),
                new CreateIndexOptions { Name = "correlation_observed" }),
            new CreateIndexModel<OutcomeDocument>(
                Builders<OutcomeDocument>.IndexKeys.Ascending(item => item.TenantId)
                    .Ascending(item => item.ObservedAtUtc),
                new CreateIndexOptions { Name = "tenant_observed" })
        ], cancellationToken).ConfigureAwait(false);
        await _snapshots.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<SnapshotDocument>(
                Builders<SnapshotDocument>.IndexKeys.Ascending(item => item.FeatureSchemaVersion)
                    .Ascending(item => item.SnapshotAtUtc),
                new CreateIndexOptions { Name = "schema_snapshot_at" }),
            new CreateIndexModel<SnapshotDocument>(
                Builders<SnapshotDocument>.IndexKeys.Ascending(item => item.EmailCorrelationId)
                    .Ascending(item => item.SnapshotAtUtc),
                new CreateIndexOptions { Name = "correlation_snapshot_at" })
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppendObservationResult> AppendAsync(
        EmailDeliveryOutcomeObservation observation,
        CancellationToken cancellationToken = default)
    {
        var existing = await _outcomes.Find(item => item.Id == observation.OutcomeEventId).FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return existing.PayloadJson == JsonSerializer.Serialize(observation, JsonOptions)
                ? AppendObservationResult.Duplicate : AppendObservationResult.Conflict;
        var natural = Builders<OutcomeDocument>.Filter.Eq(item => item.EmailCorrelationId, observation.EmailCorrelationId) &
            Builders<OutcomeDocument>.Filter.Eq(item => item.OutcomeSource, observation.OutcomeSource) &
            Builders<OutcomeDocument>.Filter.Eq(item => item.SendAttemptAtUtc, observation.SendAttemptAtUtc.UtcDateTime) &
            Builders<OutcomeDocument>.Filter.Eq(item => item.ObservedAtUtc, observation.ObservedAtUtc.UtcDateTime);
        var naturalDocument = await _outcomes.Find(natural).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (naturalDocument is not null && string.Equals(
            naturalDocument.Outcome, observation.Outcome.ToString(), StringComparison.Ordinal))
            return AppendObservationResult.Duplicate;
        var conflict = naturalDocument is not null;
        try
        {
            await _outcomes.InsertOneAsync(OutcomeDocument.FromModel(observation), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return conflict ? AppendObservationResult.Conflict : AppendObservationResult.Inserted;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return AppendObservationResult.Duplicate;
        }
    }

    async Task<bool> IEmailValidationFeatureSnapshotStore.AppendAsync(
        EmailValidationFeatureSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await _snapshots.InsertOneAsync(SnapshotDocument.FromModel(snapshot), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<EmailDeliveryOutcomeObservation>> QueryAsync(
        DateTimeOffset observedFromUtc,
        DateTimeOffset observedThroughUtc,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<OutcomeDocument>.Filter.Gte(item => item.ObservedAtUtc, observedFromUtc.UtcDateTime) &
            Builders<OutcomeDocument>.Filter.Lte(item => item.ObservedAtUtc, observedThroughUtc.UtcDateTime);
        if (tenantId is not null) filter &= Builders<OutcomeDocument>.Filter.Eq(item => item.TenantId, tenantId);
        var documents = await _outcomes.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(item => item.ToModel()).ToArray();
    }

    public async Task<IReadOnlyList<EmailValidationFeatureSnapshot>> QueryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        string featureSchemaVersion,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<SnapshotDocument>.Filter.Eq(item => item.FeatureSchemaVersion, featureSchemaVersion) &
            Builders<SnapshotDocument>.Filter.Gte(item => item.SnapshotAtUtc, fromUtc.UtcDateTime) &
            Builders<SnapshotDocument>.Filter.Lte(item => item.SnapshotAtUtc, throughUtc.UtcDateTime);
        if (tenantId is not null) filter &= Builders<SnapshotDocument>.Filter.Eq(item => item.TenantId, tenantId);
        var documents = await _snapshots.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(item => item.ToModel()).ToArray();
    }

    [BsonIgnoreExtraElements]
    internal sealed class OutcomeDocument
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string EmailCorrelationId { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public string OutcomeSource { get; set; } = string.Empty;
        public DateTime SendAttemptAtUtc { get; set; }
        public DateTime ObservedAtUtc { get; set; }
        public string Outcome { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;

        public static OutcomeDocument FromModel(EmailDeliveryOutcomeObservation model) => new()
        {
            Id = model.OutcomeEventId,
            EmailCorrelationId = model.EmailCorrelationId,
            TenantId = model.TenantId,
            OutcomeSource = model.OutcomeSource,
            SendAttemptAtUtc = model.SendAttemptAtUtc.UtcDateTime,
            ObservedAtUtc = model.ObservedAtUtc.UtcDateTime,
            Outcome = model.Outcome.ToString(),
            PayloadJson = JsonSerializer.Serialize(model, JsonOptions)
        };

        public EmailDeliveryOutcomeObservation ToModel() =>
            JsonSerializer.Deserialize<EmailDeliveryOutcomeObservation>(PayloadJson, JsonOptions) ??
            throw new InvalidOperationException($"Outcome observation '{Id}' could not be deserialized.");
    }

    [BsonIgnoreExtraElements]
    internal sealed class SnapshotDocument
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string ValidationId { get; set; } = string.Empty;
        public string EmailCorrelationId { get; set; } = string.Empty;
        public string DomainCorrelationId { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public DateTime SnapshotAtUtc { get; set; }
        public string FeatureSchemaVersion { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;

        public static SnapshotDocument FromModel(EmailValidationFeatureSnapshot model) => new()
        {
            Id = model.SnapshotId,
            ValidationId = model.ValidationId,
            EmailCorrelationId = model.EmailCorrelationId,
            DomainCorrelationId = model.DomainCorrelationId,
            TenantId = model.TenantId,
            SnapshotAtUtc = model.SnapshotAtUtc.UtcDateTime,
            FeatureSchemaVersion = model.FeatureSchemaVersion,
            PayloadJson = JsonSerializer.Serialize(model, JsonOptions)
        };

        public EmailValidationFeatureSnapshot ToModel() =>
            JsonSerializer.Deserialize<EmailValidationFeatureSnapshot>(PayloadJson, JsonOptions) ??
            throw new InvalidOperationException($"Feature snapshot '{Id}' could not be deserialized.");
    }
}

public sealed class ClassificationPersistenceInitializer(
    MongoValidationIntelligenceStore validationStore,
    MongoClassificationEvidenceStore classificationStore,
    IOptions<EmailValidationOptions> options) : IEmailValidationPersistenceInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var persistence = options.Value.Persistence;
        if (!persistence.Enabled || !string.Equals(persistence.Provider, "MongoDB", StringComparison.OrdinalIgnoreCase))
            return;
        await validationStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await classificationStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }
}
