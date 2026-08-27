using System.Collections.Concurrent;
using EmailValidation.Core;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public sealed class InMemorySmtpReputationStateStore : ISmtpReputationStateStore
{
    private readonly ConcurrentDictionary<string, SmtpReputationScopeSnapshot> _states = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<SmtpReputationScopeSnapshot>> GetManyAsync(
        IReadOnlyList<(SmtpReputationScopeType ScopeType, string ScopeId)> scopes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SmtpReputationScopeSnapshot> result = scopes
            .Select(scope => _states.TryGetValue(Id(scope.ScopeType, scope.ScopeId), out var state) ? state : null)
            .Where(state => state is not null)
            .Cast<SmtpReputationScopeSnapshot>()
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<SmtpReputationStateWriteResult> TrySaveAsync(
        SmtpReputationScopeSnapshot state,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Id(state.ScopeType, state.ScopeId);
        while (true)
        {
            if (!_states.TryGetValue(id, out var current))
            {
                if (expectedVersion != 0) return Task.FromResult(new SmtpReputationStateWriteResult(false, null));
                if (_states.TryAdd(id, state))
                    return Task.FromResult(new SmtpReputationStateWriteResult(true, state));
                continue;
            }
            if (current.Version != expectedVersion)
                return Task.FromResult(new SmtpReputationStateWriteResult(false, current));
            if (_states.TryUpdate(id, state, current))
                return Task.FromResult(new SmtpReputationStateWriteResult(true, state));
        }
    }

    private static string Id(SmtpReputationScopeType type, string id) => $"{type}|{id}";
}

public sealed class MongoSmtpReputationStateStore : ISmtpReputationStateStore
{
    private readonly IMongoCollection<SmtpReputationStateDocument> _collection;

    public MongoSmtpReputationStateStore(IMongoClient client, IOptions<EmailValidationOptions> options)
    {
        var persistence = options.Value.Persistence;
        _collection = client.GetDatabase(persistence.DatabaseName)
            .GetCollection<SmtpReputationStateDocument>(persistence.SmtpReputationStateCollection);
    }

    public async Task<IReadOnlyList<SmtpReputationScopeSnapshot>> GetManyAsync(
        IReadOnlyList<(SmtpReputationScopeType ScopeType, string ScopeId)> scopes,
        CancellationToken cancellationToken = default)
    {
        if (scopes.Count == 0) return [];
        var ids = scopes.Select(scope => Id(scope.ScopeType, scope.ScopeId)).ToArray();
        var documents = await _collection.Find(
                Builders<SmtpReputationStateDocument>.Filter.In(item => item.Id, ids))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(document => document.ToModel()).ToArray();
    }

    public async Task<SmtpReputationStateWriteResult> TrySaveAsync(
        SmtpReputationScopeSnapshot state,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var document = SmtpReputationStateDocument.FromModel(state);
        try
        {
            if (expectedVersion == 0)
            {
                await _collection.InsertOneAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new(true, state);
            }
            var result = await _collection.ReplaceOneAsync(
                item => item.Id == document.Id && item.Version == expectedVersion,
                document,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ModifiedCount == 1 ? new(true, state) : new(false, null);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new(false, null);
        }
    }

    private static string Id(SmtpReputationScopeType type, string id) =>
        $"{type}|{id.Trim().ToLowerInvariant()}";

    internal sealed class SmtpReputationStateDocument
    {
        [BsonId]
        public required string Id { get; init; }
        public SmtpReputationScopeType ScopeType { get; init; }
        public required string ScopeId { get; init; }
        public MailProvider Provider { get; init; }
        public SmtpReputationState State { get; init; }
        public DateTimeOffset WindowStartedAtUtc { get; init; }
        public int ConnectionCount { get; init; }
        public int RcptCount { get; init; }
        public int UnknownRecipientCount { get; init; }
        public int PolicyBlockCount { get; init; }
        public int TemporaryDeferralCount { get; init; }
        public int ConnectionFailureCount { get; init; }
        public List<string> AffectedIdentityIds { get; init; } = [];
        public List<string> AffectedProviders { get; init; } = [];
        public DateTimeOffset? LastLiveSmtpAttemptAtUtc { get; init; }
        public DateTimeOffset? CooldownUntilUtc { get; init; }
        public DateTimeOffset? LastHealthyAtUtc { get; init; }
        public DateTimeOffset? LastStateChangedAtUtc { get; init; }
        public int HalfOpenProbeCount { get; init; }
        public int ConsecutiveRecoverySuccesses { get; init; }
        public string PolicyVersion { get; init; } = string.Empty;
        public long Version { get; init; }

        public SmtpReputationScopeSnapshot ToModel() => new()
        {
            ScopeType = ScopeType,
            ScopeId = ScopeId,
            Provider = Provider,
            State = State,
            WindowStartedAtUtc = WindowStartedAtUtc,
            ConnectionCount = ConnectionCount,
            RcptCount = RcptCount,
            UnknownRecipientCount = UnknownRecipientCount,
            PolicyBlockCount = PolicyBlockCount,
            TemporaryDeferralCount = TemporaryDeferralCount,
            ConnectionFailureCount = ConnectionFailureCount,
            AffectedIdentityIds = AffectedIdentityIds,
            AffectedProviders = AffectedProviders,
            LastLiveSmtpAttemptAtUtc = LastLiveSmtpAttemptAtUtc,
            CooldownUntilUtc = CooldownUntilUtc,
            LastHealthyAtUtc = LastHealthyAtUtc,
            LastStateChangedAtUtc = LastStateChangedAtUtc,
            HalfOpenProbeCount = HalfOpenProbeCount,
            ConsecutiveRecoverySuccesses = ConsecutiveRecoverySuccesses,
            PolicyVersion = PolicyVersion,
            Version = Version
        };

        public static SmtpReputationStateDocument FromModel(SmtpReputationScopeSnapshot model) => new()
        {
            Id = MongoSmtpReputationStateStore.Id(model.ScopeType, model.ScopeId),
            ScopeType = model.ScopeType,
            ScopeId = model.ScopeId,
            Provider = model.Provider,
            State = model.State,
            WindowStartedAtUtc = model.WindowStartedAtUtc,
            ConnectionCount = model.ConnectionCount,
            RcptCount = model.RcptCount,
            UnknownRecipientCount = model.UnknownRecipientCount,
            PolicyBlockCount = model.PolicyBlockCount,
            TemporaryDeferralCount = model.TemporaryDeferralCount,
            ConnectionFailureCount = model.ConnectionFailureCount,
            AffectedIdentityIds = model.AffectedIdentityIds.ToList(),
            AffectedProviders = model.AffectedProviders.ToList(),
            LastLiveSmtpAttemptAtUtc = model.LastLiveSmtpAttemptAtUtc,
            CooldownUntilUtc = model.CooldownUntilUtc,
            LastHealthyAtUtc = model.LastHealthyAtUtc,
            LastStateChangedAtUtc = model.LastStateChangedAtUtc,
            HalfOpenProbeCount = model.HalfOpenProbeCount,
            ConsecutiveRecoverySuccesses = model.ConsecutiveRecoverySuccesses,
            PolicyVersion = model.PolicyVersion,
            Version = model.Version
        };
    }
}
