using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailValidation.Core;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

/// <summary>Fresh shared suppression reads; tenant decisions never enter mailbox/domain caches.</summary>
public sealed class MongoSuppressionStore : IGlobalSuppressionStore
{
    private readonly IMongoCollection<Document> _entries;
    private readonly JsonValidationIntelligenceStore _legacy;

    public MongoSuppressionStore(IMongoClient client, IOptions<EmailValidationOptions> options,
        JsonValidationIntelligenceStore legacy)
    {
        _legacy = legacy;
        _entries = client.GetDatabase(options.Value.Persistence.DatabaseName)
            .GetCollection<Document>(options.Value.Persistence.SuppressionCollection);
    }

    public Task<SuppressionEntry?> GetAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        GetScopedAsync(normalizedEmail, null, cancellationToken);

    public async Task<SuppressionEntry?> GetScopedAsync(string normalizedEmail, string? tenantId,
        CancellationToken cancellationToken = default)
    {
        var identity = MailboxIdentity.Create(normalizedEmail);
        var globalId = Key(identity.Key, null);
        var ids = tenantId is null ? new[] { globalId } : [Key(identity.Key, tenantId), globalId];
        var matches = await _entries.Find(Builders<Document>.Filter.In(entry => entry.Id, ids))
            .Limit(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        var match = matches.FirstOrDefault(entry => entry.TenantId == tenantId) ?? matches.FirstOrDefault();
        if (match is not null) return match.ToModel();
        // Only identity-v2 legacy evidence is eligible. Insert-only migration cannot
        // overwrite a newer shared suppression written by another host.
        var legacy = await _legacy.GetScopedAsync(normalizedEmail, tenantId, cancellationToken).ConfigureAwait(false);
        if (legacy is null) return null;
        var document = Document.From(legacy);
        await _entries.UpdateOneAsync(entry => entry.Id == document.Id,
            Builders<Document>.Update.SetOnInsert(entry => entry.Entry, document.Entry)
                .SetOnInsert(entry => entry.TenantId, document.TenantId),
            new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
        return (await _entries.Find(entry => entry.Id == document.Id).FirstAsync(cancellationToken).ConfigureAwait(false)).ToModel();
    }

    public Task AddAsync(SuppressionEntry entry, CancellationToken cancellationToken = default)
    {
        var document = Document.From(entry);
        return _entries.ReplaceOneAsync(item => item.Id == document.Id, document,
            new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    private static string Key(string mailboxKey, string? tenantId) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { tenantId, mailboxKey }))));

    internal sealed class Document
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public string Entry { get; set; } = string.Empty;
        public SuppressionEntry ToModel() => JsonSerializer.Deserialize<SuppressionEntry>(Entry) ??
            throw new InvalidDataException("Suppression entry is empty.");
        public static Document From(SuppressionEntry entry)
        {
            var identity = MailboxIdentity.Create(entry.NormalizedEmail);
            return new() { Id = Key(identity.Key, entry.TenantId), TenantId = entry.TenantId,
                Entry = JsonSerializer.Serialize(entry with { NormalizedEmail = identity.Address, MailboxKey = identity.Key }) };
        }
    }
}
