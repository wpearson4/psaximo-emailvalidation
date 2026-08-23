using System.Collections.Concurrent;
using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public sealed class MongoCommercialResourceStore :
    ICommercialResourceStore,
    ICommercialResourceInfrastructureInitializer
{
    private const string OwnershipKind = "ownership";
    private const string IdempotencyKind = "idempotency";
    private readonly IMongoCollection<Document> _collection;

    public MongoCommercialResourceStore(IMongoClient client, IOptions<EmailValidationOptions> options)
    {
        var persistence = options.Value.Persistence;
        _collection = client.GetDatabase(persistence.DatabaseName)
            .GetCollection<Document>(persistence.CommercialResourceCollection);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var indexes = new[]
        {
            new CreateIndexModel<Document>(
                Builders<Document>.IndexKeys
                    .Ascending(value => value.Kind)
                    .Ascending(value => value.ResourceType)
                    .Ascending(value => value.ResourceId)
                    .Ascending(value => value.PrincipalKey),
                new CreateIndexOptions<Document>
                {
                    Name = "ux_commercial_resource_owner",
                    Unique = true,
                    PartialFilterExpression = Builders<Document>.Filter.Eq(value => value.Kind, OwnershipKind)
                }),
            new CreateIndexModel<Document>(
                Builders<Document>.IndexKeys
                    .Ascending(value => value.Kind)
                    .Ascending(value => value.PrincipalKey)
                    .Ascending(value => value.Operation)
                    .Ascending(value => value.IdempotencyKey),
                new CreateIndexOptions<Document>
                {
                    Name = "ux_commercial_idempotency",
                    Unique = true,
                    PartialFilterExpression = Builders<Document>.Filter.Eq(value => value.Kind, IdempotencyKind)
                })
        };
        await _collection.Indexes.CreateManyAsync(indexes, cancellationToken).ConfigureAwait(false);
    }

    public async Task GrantAsync(ResourceOwnership ownership, CancellationToken cancellationToken = default)
    {
        var filter = Builders<Document>.Filter.And(
            Builders<Document>.Filter.Eq(value => value.Kind, OwnershipKind),
            Builders<Document>.Filter.Eq(value => value.ResourceType, ownership.ResourceType),
            Builders<Document>.Filter.Eq(value => value.ResourceId, ownership.ResourceId),
            Builders<Document>.Filter.Eq(value => value.PrincipalKey, ownership.PrincipalKey));
        await _collection.ReplaceOneAsync(filter, Document.FromOwnership(ownership),
            new ReplaceOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> HasAccessAsync(
        OwnedResourceType resourceType,
        string resourceId,
        string principalKey,
        CancellationToken cancellationToken = default) =>
        _collection.Find(value => value.Kind == OwnershipKind &&
                value.ResourceType == resourceType && value.ResourceId == resourceId &&
                value.PrincipalKey == principalKey)
            .AnyAsync(cancellationToken);

    public async Task<IdempotentOperation?> GetIdempotentOperationAsync(
        string principalKey,
        string operation,
        string key,
        CancellationToken cancellationToken = default)
    {
        var document = await _collection.Find(value => value.Kind == IdempotencyKind &&
                value.PrincipalKey == principalKey && value.Operation == operation &&
                value.IdempotencyKey == key)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document?.ToIdempotentOperation();
    }

    public async Task<bool> TrySaveIdempotentOperationAsync(
        IdempotentOperation operation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _collection.InsertOneAsync(Document.FromIdempotency(operation),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    internal sealed class Document
    {
        [BsonId] public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public required string Kind { get; init; }
        public OwnedResourceType? ResourceType { get; init; }
        public string? ResourceId { get; init; }
        public required string PrincipalKey { get; init; }
        public string? SubjectId { get; init; }
        public string? TenantId { get; init; }
        public string? Operation { get; init; }
        public string? IdempotencyKey { get; init; }
        public string? RequestHash { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }

        public static Document FromOwnership(ResourceOwnership ownership) => new()
        {
            Kind = OwnershipKind,
            ResourceType = ownership.ResourceType,
            ResourceId = ownership.ResourceId,
            PrincipalKey = ownership.PrincipalKey,
            SubjectId = ownership.SubjectId,
            TenantId = ownership.TenantId,
            CreatedAtUtc = ownership.CreatedAtUtc
        };

        public static Document FromIdempotency(IdempotentOperation operation) => new()
        {
            Kind = IdempotencyKind,
            PrincipalKey = operation.PrincipalKey,
            Operation = operation.Operation,
            IdempotencyKey = operation.Key,
            RequestHash = operation.RequestHash,
            ResourceId = operation.ResourceId,
            CreatedAtUtc = operation.CreatedAtUtc
        };

        public IdempotentOperation ToIdempotentOperation() => new(
            PrincipalKey,
            Operation!,
            IdempotencyKey!,
            RequestHash!,
            ResourceId!,
            CreatedAtUtc);
    }
}

public sealed class InMemoryCommercialResourceStore :
    ICommercialResourceStore,
    ICommercialResourceInfrastructureInitializer
{
    private readonly ConcurrentDictionary<(OwnedResourceType, string, string), ResourceOwnership> _ownership = [];
    private readonly ConcurrentDictionary<(string, string, string), IdempotentOperation> _idempotency = [];

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task GrantAsync(ResourceOwnership ownership, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ownership[(ownership.ResourceType, ownership.ResourceId, ownership.PrincipalKey)] = ownership;
        return Task.CompletedTask;
    }

    public Task<bool> HasAccessAsync(
        OwnedResourceType resourceType,
        string resourceId,
        string principalKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_ownership.ContainsKey((resourceType, resourceId, principalKey)));
    }

    public Task<IdempotentOperation?> GetIdempotentOperationAsync(
        string principalKey,
        string operation,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_idempotency.GetValueOrDefault((principalKey, operation, key)));
    }

    public Task<bool> TrySaveIdempotentOperationAsync(
        IdempotentOperation operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_idempotency.TryAdd(
            (operation.PrincipalKey, operation.Operation, operation.Key), operation));
    }
}
