using EmailValidation.Core;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public sealed class MongoOutboundIdentityHealthStore : IOutboundIdentityHealthStore
{
    private readonly IMongoCollection<OutboundIdentityHealthDocument> _collection;
    private readonly OutboundIdentityHealthPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public MongoOutboundIdentityHealthStore(
        IMongoClient client,
        IOptions<EmailValidationOptions> options,
        OutboundIdentityHealthPolicy policy,
        TimeProvider timeProvider)
    {
        var persistence = options.Value.Persistence;
        _collection = client.GetDatabase(persistence.DatabaseName)
            .GetCollection<OutboundIdentityHealthDocument>(persistence.OutboundIdentityHealthCollection);
        _policy = policy;
        _timeProvider = timeProvider;
    }

    public async Task<OutboundIdentityHealth> GetAsync(
        string identityId,
        MailProvider provider,
        CancellationToken cancellationToken = default)
    {
        var id = CreateId(identityId, provider);
        var document = await _collection.Find(item => item.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (document is null)
            return new(identityId, provider, OutboundIdentityHealthState.Healthy);
        var state = document.ToModel();
        if (state.State is OutboundIdentityHealthState.Cooldown or OutboundIdentityHealthState.Quarantined &&
            state.CooldownUntil <= _timeProvider.GetUtcNow())
        {
            state = state with
            {
                State = OutboundIdentityHealthState.Healthy,
                CooldownUntil = null,
                AttributableFailureCount = 0,
                Reason = null
            };
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        return state;
    }

    public async Task RecordAsync(
        OutboundIdentityOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        var provider = !outcome.Global &&
            outcome.CooldownScope is SmtpCooldownScope.OutboundIdentity or SmtpCooldownScope.SourceIp
                ? outcome.Provider
                : MailProvider.Unknown;
        var current = await GetAsync(outcome.IdentityId, provider, cancellationToken).ConfigureAwait(false);
        var next = _policy.Evaluate(outcome, provider, current.AttributableFailureCount);
        await SaveAsync(next, cancellationToken).ConfigureAwait(false);
    }

    private Task<ReplaceOneResult> SaveAsync(
        OutboundIdentityHealth health,
        CancellationToken cancellationToken) =>
        _collection.ReplaceOneAsync(
            item => item.Id == CreateId(health.IdentityId, health.Provider),
            OutboundIdentityHealthDocument.FromModel(health, _timeProvider.GetUtcNow()),
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);

    private static string CreateId(string identityId, MailProvider provider) =>
        $"{identityId.Trim().ToLowerInvariant()}|{provider}";

    internal sealed class OutboundIdentityHealthDocument
    {
        [BsonId]
        public required string Id { get; init; }
        public required string IdentityId { get; init; }
        public MailProvider Provider { get; init; }
        public OutboundIdentityHealthState State { get; init; }
        public DateTimeOffset? CooldownUntil { get; init; }
        public int AttributableFailureCount { get; init; }
        public string? Reason { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; init; }

        public OutboundIdentityHealth ToModel() => new(
            IdentityId, Provider, State, CooldownUntil, AttributableFailureCount, Reason);

        public static OutboundIdentityHealthDocument FromModel(
            OutboundIdentityHealth model,
            DateTimeOffset updatedAtUtc) => new()
        {
            Id = CreateId(model.IdentityId, model.Provider),
            IdentityId = model.IdentityId,
            Provider = model.Provider,
            State = model.State,
            CooldownUntil = model.CooldownUntil,
            AttributableFailureCount = model.AttributableFailureCount,
            Reason = model.Reason,
            UpdatedAtUtc = updatedAtUtc
        };
    }
}
