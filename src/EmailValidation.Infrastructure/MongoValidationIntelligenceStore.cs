using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public sealed class EmailValidationPersistenceException(string message, Exception innerException)
    : Exception(message, innerException);

public sealed class NoOpEmailValidationPersistenceInitializer : IEmailValidationPersistenceInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Mongo-backed implementation of the existing host-agnostic intelligence contracts.
/// Domain observations are embedded in the domain document so the feature owns only
/// the two dedicated collections needed for reusable validation intelligence.
/// </summary>
public sealed class MongoValidationIntelligenceStore :
    IValidationIntelligenceStore,
    IValidationObservationStore,
    IEmailValidationPersistenceInitializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<DomainIntelligenceDocument> _domains;
    private readonly IMongoCollection<MailboxIntelligenceDocument> _mailboxes;
    private readonly PersistenceOptions _options;
    private readonly IValidationPersistenceMetrics _metrics;
    private readonly ILogger<MongoValidationIntelligenceStore> _logger;
    private readonly ConcurrentDictionary<string, DomainIntelligence> _domainCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MailboxIntelligence> _mailboxCache =
        new(StringComparer.OrdinalIgnoreCase);

    public MongoValidationIntelligenceStore(
        IMongoClient client,
        IOptions<EmailValidationOptions> options,
        IValidationPersistenceMetrics metrics,
        ILogger<MongoValidationIntelligenceStore> logger)
    {
        _options = options.Value.Persistence;
        _metrics = metrics;
        _logger = logger;
        _database = client.GetDatabase(_options.DatabaseName);
        _domains = _database.GetCollection<DomainIntelligenceDocument>(_options.DomainCollection);
        _mailboxes = _database.GetCollection<MailboxIntelligenceDocument>(_options.MailboxCollection);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _database.RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1), cancellationToken: cancellationToken).ConfigureAwait(false);

            var domainIndexes = new[]
            {
                Index("ux_domain_normalized", Builders<DomainIntelligenceDocument>.IndexKeys.Ascending(x => x.NormalizedDomain), unique: true),
                Index("ix_domain_provider", Builders<DomainIntelligenceDocument>.IndexKeys.Ascending(x => x.Provider)),
                Index("ix_domain_last_validated", Builders<DomainIntelligenceDocument>.IndexKeys.Ascending(x => x.LastValidatedAt)),
                Index("ix_domain_updated", Builders<DomainIntelligenceDocument>.IndexKeys.Ascending(x => x.UpdatedAt))
            };
            var mailboxIndexes = new[]
            {
                Index("ux_mailbox_normalized", Builders<MailboxIntelligenceDocument>.IndexKeys.Ascending(x => x.NormalizedEmail), unique: true),
                Index("ix_mailbox_domain", Builders<MailboxIntelligenceDocument>.IndexKeys.Ascending(x => x.Domain)),
                Index("ix_mailbox_last_validated", Builders<MailboxIntelligenceDocument>.IndexKeys.Ascending(x => x.LastValidatedAt)),
                Index("ix_mailbox_status", Builders<MailboxIntelligenceDocument>.IndexKeys.Ascending(x => x.LastStatus)),
                Index("ix_mailbox_updated", Builders<MailboxIntelligenceDocument>.IndexKeys.Ascending(x => x.UpdatedAt))
            };

            await _domains.Indexes.CreateManyAsync(domainIndexes, cancellationToken).ConfigureAwait(false);
            await _mailboxes.Indexes.CreateManyAsync(mailboxIndexes, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Mongo validation intelligence initialized in database {Database}; collections {DomainCollection} and {MailboxCollection}",
                _options.DatabaseName, _options.DomainCollection, _options.MailboxCollection);
        }
        catch (MongoException exception)
        {
            throw new EmailValidationPersistenceException(
                $"Mongo validation intelligence initialization failed for database '{_options.DatabaseName}'. Verify App Configuration, Key Vault access, and Mongo connectivity.",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new EmailValidationPersistenceException(
                $"Mongo validation intelligence initialization timed out for database '{_options.DatabaseName}'.",
                exception);
        }
    }

    public async Task<DomainIntelligence?> GetDomainAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDomain(domain);
        if (_domainCache.TryGetValue(normalized, out var cached)) return cached;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var document = await _domains.Find(x => x.Id == normalized)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var model = document?.ToModel();
            if (model is not null) _domainCache[normalized] = model;
            _metrics.RecordRead("domain", model is not null, stopwatch.Elapsed);
            return model;
        }
        catch (MongoException exception)
        {
            _metrics.RecordRead("domain", false, stopwatch.Elapsed);
            LogUnavailable("read domain intelligence", exception);
            return null;
        }
        catch (TimeoutException exception)
        {
            _metrics.RecordRead("domain", false, stopwatch.Elapsed);
            LogUnavailable("read domain intelligence", exception);
            return null;
        }
    }

    public async Task<MailboxIntelligence?> GetMailboxAsync(
        string normalizedEmail,
        CancellationToken cancellationToken = default)
    {
        normalizedEmail = NormalizeEmail(normalizedEmail);
        if (_mailboxCache.TryGetValue(normalizedEmail, out var cached)) return cached;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var id = Hash(normalizedEmail);
            var document = await _mailboxes.Find(x => x.Id == id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var model = document?.ToModel();
            if (model is not null) _mailboxCache[normalizedEmail] = model;
            _metrics.RecordRead("mailbox", model is not null, stopwatch.Elapsed);
            return model;
        }
        catch (MongoException exception)
        {
            _metrics.RecordRead("mailbox", false, stopwatch.Elapsed);
            LogUnavailable("read mailbox intelligence", exception);
            return null;
        }
        catch (TimeoutException exception)
        {
            _metrics.RecordRead("mailbox", false, stopwatch.Elapsed);
            LogUnavailable("read mailbox intelligence", exception);
            return null;
        }
    }

    public async Task SaveDomainAsync(
        DomainIntelligence intelligence,
        CancellationToken cancellationToken = default)
    {
        var document = DomainIntelligenceDocument.FromModel(intelligence);
        var update = Builders<DomainIntelligenceDocument>.Update
            .Set(x => x.Domain, document.Domain)
            .Set(x => x.NormalizedDomain, document.NormalizedDomain)
            .Set(x => x.MxRecords, document.MxRecords)
            .Set(x => x.MxTopologyFingerprint, document.MxTopologyFingerprint)
            .Set(x => x.Provider, document.Provider)
            .Set(x => x.GatewayProvider, document.GatewayProvider)
            .Set(x => x.ProviderConfidence, document.ProviderConfidence)
            .Set(x => x.CatchAllStatus, document.CatchAllStatus)
            .Set(x => x.CatchAllConfidence, document.CatchAllConfidence)
            .Set(x => x.VerificationReliability, document.VerificationReliability)
            .Set(x => x.ResultStability, document.ResultStability)
            .Set(x => x.LastObservedAt, document.LastObservedAt)
            .Set(x => x.LastValidatedAt, document.LastValidatedAt)
            .Set(x => x.EvidenceFreshUntil, document.EvidenceFreshUntil)
            .Set(x => x.ProviderStrategyVersion, document.ProviderStrategyVersion)
            .Set(x => x.PayloadJson, document.PayloadJson)
            .Set(x => x.UpdatedAt, document.UpdatedAt)
            .SetOnInsert(x => x.CreatedAt, document.CreatedAt);
        try
        {
            await _domains.UpdateOneAsync(
                x => x.Id == document.Id,
                update,
                new UpdateOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
            _domainCache[document.NormalizedDomain] = document.ToModel()!;
            _metrics.RecordWrite("domain", true);
        }
        catch (MongoException exception)
        {
            _metrics.RecordWrite("domain", false);
            LogUnavailable("write domain intelligence", exception);
        }
        catch (TimeoutException exception)
        {
            _metrics.RecordWrite("domain", false);
            LogUnavailable("write domain intelligence", exception);
        }
    }

    public async Task SaveMailboxAsync(
        MailboxIntelligence intelligence,
        CancellationToken cancellationToken = default)
    {
        var document = MailboxIntelligenceDocument.FromModel(intelligence);
        var updates = new List<UpdateDefinition<MailboxIntelligenceDocument>>
        {
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.NormalizedEmail, document.NormalizedEmail),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.Domain, document.Domain),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.LastStatus, document.LastStatus),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.LastSubStatus, document.LastSubStatus),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.LastConfidence, document.LastConfidence),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.ConfidenceType, document.ConfidenceType),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.ConfidenceReason, document.ConfidenceReason),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.LastMailboxResult, document.LastMailboxResult),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.LastValidatedAt, document.LastValidatedAt),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.ReasonCodes, document.ReasonCodes),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.ProviderAtValidation, document.ProviderAtValidation),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.CatchAllStatusAtValidation, document.CatchAllStatusAtValidation),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.VerificationReliabilityAtValidation, document.VerificationReliabilityAtValidation),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.ValidationEngineVersion, document.ValidationEngineVersion),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.ClassificationPolicyVersion, document.ClassificationPolicyVersion),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.ConfidenceModelVersion, document.ConfidenceModelVersion),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.ProviderStrategyVersion, document.ProviderStrategyVersion),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.MxTopologyFingerprint, document.MxTopologyFingerprint),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.UsedLiveSmtp, document.UsedLiveSmtp),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.PayloadJson, document.PayloadJson),
            Builders<MailboxIntelligenceDocument>.Update.Set(x => x.UpdatedAt, document.UpdatedAt),
            Builders<MailboxIntelligenceDocument>.Update.SetOnInsert(x => x.CreatedAt, document.CreatedAt)
        };
        if (document.LastStrongPositiveAt is not null)
            updates.Add(Builders<MailboxIntelligenceDocument>.Update.Set(x => x.LastStrongPositiveAt, document.LastStrongPositiveAt));
        if (document.LastStrongNegativeAt is not null)
            updates.Add(Builders<MailboxIntelligenceDocument>.Update.Set(x => x.LastStrongNegativeAt, document.LastStrongNegativeAt));

        try
        {
            var stored = await _mailboxes.FindOneAndUpdateAsync(
                Builders<MailboxIntelligenceDocument>.Filter.Eq(x => x.Id, document.Id),
                Builders<MailboxIntelligenceDocument>.Update.Combine(updates),
                new FindOneAndUpdateOptions<MailboxIntelligenceDocument, MailboxIntelligenceDocument>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken).ConfigureAwait(false);
            _mailboxCache[document.NormalizedEmail] = stored.ToModel()!;
            _metrics.RecordWrite("mailbox", true);
        }
        catch (MongoException exception)
        {
            _metrics.RecordWrite("mailbox", false);
            LogUnavailable("write mailbox intelligence", exception);
        }
        catch (TimeoutException exception)
        {
            _metrics.RecordWrite("mailbox", false);
            LogUnavailable("write mailbox intelligence", exception);
        }
    }

    public async Task<IReadOnlyList<ValidationObservation>> GetDomainObservationsAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var normalized = NormalizeDomain(domain);
            var document = await _domains.Find(x => x.Id == normalized)
                .Project(x => x.Observations)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var observations = document?.Select(x => x.ToModel()).ToArray() ?? [];
            _metrics.RecordRead("domain-observations", observations.Length > 0, stopwatch.Elapsed);
            return observations;
        }
        catch (MongoException exception)
        {
            _metrics.RecordRead("domain-observations", false, stopwatch.Elapsed);
            LogUnavailable("read domain observations", exception);
            return [];
        }
        catch (TimeoutException exception)
        {
            _metrics.RecordRead("domain-observations", false, stopwatch.Elapsed);
            LogUnavailable("read domain observations", exception);
            return [];
        }
    }

    public async Task RecordAsync(
        ValidationObservation observation,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDomain(observation.Domain);
        var now = DateTime.UtcNow;
        var update = Builders<DomainIntelligenceDocument>.Update.Combine(
            Builders<DomainIntelligenceDocument>.Update.PushEach(
                x => x.Observations,
                [ValidationObservationDocument.FromModel(observation)],
                slice: -Math.Max(1, _options.MaximumObservationsPerDomain)),
            Builders<DomainIntelligenceDocument>.Update.Inc(x => x.ObservationCount, 1),
            Builders<DomainIntelligenceDocument>.Update.Set(x => x.UpdatedAt, now),
            Builders<DomainIntelligenceDocument>.Update.SetOnInsert(x => x.Domain, normalized),
            Builders<DomainIntelligenceDocument>.Update.SetOnInsert(x => x.NormalizedDomain, normalized),
            Builders<DomainIntelligenceDocument>.Update.SetOnInsert(x => x.CreatedAt, now));
        try
        {
            await _domains.UpdateOneAsync(
                x => x.Id == normalized,
                update,
                new UpdateOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
            _metrics.RecordWrite("domain-observation", true);
        }
        catch (MongoException exception)
        {
            _metrics.RecordWrite("domain-observation", false);
            LogUnavailable("write domain observation", exception);
        }
        catch (TimeoutException exception)
        {
            _metrics.RecordWrite("domain-observation", false);
            LogUnavailable("write domain observation", exception);
        }
    }

    private void LogUnavailable(string operation, Exception exception) =>
        _logger.LogWarning(
            "Mongo validation intelligence unavailable while attempting to {Operation}; validation will continue without that persistence operation ({ErrorType})",
            operation,
            exception.GetType().Name);

    private static CreateIndexModel<TDocument> Index<TDocument>(
        string name,
        IndexKeysDefinition<TDocument> keys,
        bool unique = false) => new(keys, new CreateIndexOptions { Name = name, Unique = unique });

    private static string NormalizeDomain(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();
    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal sealed class DomainIntelligenceDocument
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string NormalizedDomain { get; set; } = string.Empty;
        public List<MxRecordDocument> MxRecords { get; set; } = [];
        public string? MxTopologyFingerprint { get; set; }
        [BsonRepresentation(BsonType.String)]
        public MailProvider Provider { get; set; }
        [BsonRepresentation(BsonType.String)]
        public GatewayProvider GatewayProvider { get; set; }
        public double ProviderConfidence { get; set; }
        [BsonRepresentation(BsonType.String)]
        public CatchAllStatus CatchAllStatus { get; set; }
        public double CatchAllConfidence { get; set; }
        public double VerificationReliability { get; set; }
        public double ResultStability { get; set; }
        public int ObservationCount { get; set; }
        public DateTime LastObservedAt { get; set; }
        public DateTime LastValidatedAt { get; set; }
        public DateTime? EvidenceFreshUntil { get; set; }
        public string ProviderStrategyVersion { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? PayloadJson { get; set; }
        public List<ValidationObservationDocument> Observations { get; set; } = [];

        public static DomainIntelligenceDocument FromModel(DomainIntelligence model)
        {
            var normalized = NormalizeDomain(model.Domain);
            var sanitized = model with
            {
                CatchAll = model.CatchAll with { ProbeResults = [] }
            };
            var now = DateTime.UtcNow;
            return new DomainIntelligenceDocument
            {
                Id = normalized,
                Domain = model.Domain,
                NormalizedDomain = normalized,
                MxRecords = model.MxRecords.Select(MxRecordDocument.FromModel).ToList(),
                MxTopologyFingerprint = model.Provider.TopologyFingerprint,
                Provider = model.Provider.Provider,
                GatewayProvider = model.Provider.GatewayProvider,
                ProviderConfidence = model.Provider.Confidence,
                CatchAllStatus = model.CatchAll.Status,
                CatchAllConfidence = model.CatchAll.Confidence,
                VerificationReliability = model.Behavior?.VerificationReliability ?? 0,
                ResultStability = model.Behavior?.VerificationReliability ?? 0,
                ObservationCount = model.Behavior?.ObservationCount ?? 0,
                LastObservedAt = model.ObservedAt.UtcDateTime,
                LastValidatedAt = model.ObservedAt.UtcDateTime,
                EvidenceFreshUntil = model.EvidenceExpiresAt?.UtcDateTime,
                ProviderStrategyVersion = model.StrategyVersion,
                CreatedAt = now,
                UpdatedAt = now,
                PayloadJson = JsonSerializer.Serialize(sanitized, JsonOptions)
            };
        }

        public DomainIntelligence? ToModel() => string.IsNullOrWhiteSpace(PayloadJson)
            ? null
            : JsonSerializer.Deserialize<DomainIntelligence>(PayloadJson, JsonOptions);
    }

    internal sealed class MailboxIntelligenceDocument
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string NormalizedEmail { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        [BsonRepresentation(BsonType.String)]
        public EmailValidationStatus LastStatus { get; set; }
        [BsonRepresentation(BsonType.String)]
        public DetailedStatus LastSubStatus { get; set; }
        public double LastConfidence { get; set; }
        [BsonRepresentation(BsonType.String)]
        public ConfidenceType ConfidenceType { get; set; }
        public string? ConfidenceReason { get; set; }
        [BsonRepresentation(BsonType.String)]
        public SmtpMailboxStatus LastMailboxResult { get; set; }
        public DateTime LastValidatedAt { get; set; }
        public DateTime? LastStrongPositiveAt { get; set; }
        public DateTime? LastStrongNegativeAt { get; set; }
        public List<string> ReasonCodes { get; set; } = [];
        [BsonRepresentation(BsonType.String)]
        public MailProvider ProviderAtValidation { get; set; }
        [BsonRepresentation(BsonType.String)]
        public CatchAllStatus CatchAllStatusAtValidation { get; set; }
        [BsonRepresentation(BsonType.String)]
        public VerificationReliabilityLevel VerificationReliabilityAtValidation { get; set; }
        public string ValidationEngineVersion { get; set; } = string.Empty;
        public string ClassificationPolicyVersion { get; set; } = string.Empty;
        public string ConfidenceModelVersion { get; set; } = string.Empty;
        public string ProviderStrategyVersion { get; set; } = string.Empty;
        public string? MxTopologyFingerprint { get; set; }
        public bool UsedLiveSmtp { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string PayloadJson { get; set; } = string.Empty;

        public static MailboxIntelligenceDocument FromModel(MailboxIntelligence model)
        {
            var email = NormalizeEmail(model.NormalizedEmail);
            var domain = email[(email.LastIndexOf('@') + 1)..];
            var now = DateTime.UtcNow;
            var sanitizedResult = model.LastResult with
            {
                SmtpEvidence = null,
                SmtpSessionEvidence = null,
                MxValidation = null,
                CatchAllEvidence = model.LastResult.CatchAllEvidence is null
                    ? null
                    : model.LastResult.CatchAllEvidence with { ProbeResults = [] },
                Diagnostics = model.LastResult.Diagnostics is null
                    ? null
                    : model.LastResult.Diagnostics with { Detail = null }
            };
            var sanitized = model with { LastResult = sanitizedResult };
            return new MailboxIntelligenceDocument
            {
                Id = Hash(email),
                NormalizedEmail = email,
                Domain = domain,
                LastStatus = model.PreviousStatus,
                LastSubStatus = model.LastResult.SubStatus,
                LastConfidence = model.PreviousConfidence,
                ConfidenceType = model.PreviousConfidenceType,
                ConfidenceReason = model.LastResult.ConfidenceReason,
                LastMailboxResult = model.PreviousMailboxResult,
                LastValidatedAt = model.LastValidatedAt.UtcDateTime,
                LastStrongPositiveAt = model.LastStrongPositiveEvidenceAt?.UtcDateTime,
                LastStrongNegativeAt = model.LastStrongNegativeEvidenceAt?.UtcDateTime,
                ReasonCodes = model.ReasonCodes.Select(x => x.ToString()).ToList(),
                ProviderAtValidation = model.ProviderAtValidation,
                CatchAllStatusAtValidation = model.LastResult.Checks.CatchAll,
                VerificationReliabilityAtValidation = model.LastResult.ProviderValidation?.VerificationReliabilityLevel
                    ?? VerificationReliabilityLevel.Unknown,
                ValidationEngineVersion = model.Policy.ValidationEngineVersion,
                ClassificationPolicyVersion = model.Policy.ClassificationPolicyVersion,
                ConfidenceModelVersion = model.Policy.ConfidenceModelVersion,
                ProviderStrategyVersion = model.Policy.ProviderStrategyVersion,
                MxTopologyFingerprint = model.MxTopologyFingerprint,
                UsedLiveSmtp = model.UsedLiveSmtp,
                CreatedAt = now,
                UpdatedAt = now,
                PayloadJson = JsonSerializer.Serialize(sanitized, JsonOptions)
            };
        }

        public MailboxIntelligence? ToModel()
        {
            var model = JsonSerializer.Deserialize<MailboxIntelligence>(PayloadJson, JsonOptions);
            return model is null ? null : model with
            {
                LastStrongPositiveEvidenceAt = LastStrongPositiveAt is null
                    ? null
                    : new DateTimeOffset(DateTime.SpecifyKind(LastStrongPositiveAt.Value, DateTimeKind.Utc)),
                LastStrongNegativeEvidenceAt = LastStrongNegativeAt is null
                    ? null
                    : new DateTimeOffset(DateTime.SpecifyKind(LastStrongNegativeAt.Value, DateTimeKind.Utc))
            };
        }
    }

    internal sealed class MxRecordDocument
    {
        public int Preference { get; set; }
        public string Host { get; set; } = string.Empty;
        public static MxRecordDocument FromModel(MxRecord model) => new() { Preference = model.Preference, Host = model.Host };
    }

    internal sealed class ValidationObservationDocument
    {
        public string Domain { get; set; } = string.Empty;
        [BsonRepresentation(BsonType.String)]
        public ValidationObservationType Type { get; set; }
        [BsonRepresentation(BsonType.String)]
        public MailProvider Provider { get; set; }
        public string? MxHost { get; set; }
        [BsonRepresentation(BsonType.String)]
        public CatchAllStatus CatchAllStatus { get; set; }
        public double CatchAllConfidence { get; set; }
        [BsonRepresentation(BsonType.String)]
        public SmtpResponseCategory ResponseCategory { get; set; }
        public DateTime ObservedAt { get; set; }
        public long DurationMilliseconds { get; set; }
        public int RandomRecipientAcceptedCount { get; set; }
        public int RandomRecipientProbeCount { get; set; }
        public int RandomRecipientRejectedCount { get; set; }
        [BsonRepresentation(BsonType.String)]
        public GatewayProvider GatewayProvider { get; set; }
        public string? TopologyFingerprint { get; set; }

        public static ValidationObservationDocument FromModel(ValidationObservation model) => new()
        {
            Domain = model.Domain,
            Type = model.Type,
            Provider = model.Provider,
            MxHost = model.MxHost,
            CatchAllStatus = model.CatchAllStatus,
            CatchAllConfidence = model.CatchAllConfidence,
            ResponseCategory = model.ResponseCategory,
            ObservedAt = model.ObservedAt.UtcDateTime,
            DurationMilliseconds = model.DurationMilliseconds,
            RandomRecipientAcceptedCount = model.RandomRecipientAcceptedCount,
            RandomRecipientProbeCount = model.RandomRecipientProbeCount,
            RandomRecipientRejectedCount = model.RandomRecipientRejectedCount,
            GatewayProvider = model.GatewayProvider,
            TopologyFingerprint = model.TopologyFingerprint
        };

        public ValidationObservation ToModel() => new(
            Domain,
            Type,
            Provider,
            MxHost,
            CatchAllStatus,
            CatchAllConfidence,
            ResponseCategory,
            new DateTimeOffset(DateTime.SpecifyKind(ObservedAt, DateTimeKind.Utc)),
            DurationMilliseconds,
            RandomRecipientAcceptedCount,
            RandomRecipientProbeCount,
            RandomRecipientRejectedCount,
            GatewayProvider,
            TopologyFingerprint);
    }
}
