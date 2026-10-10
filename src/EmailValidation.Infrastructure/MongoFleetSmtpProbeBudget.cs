using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using EmailValidation.Core;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public sealed class DisabledFleetSmtpProbeBudget : IFleetSmtpProbeBudget
{
    public Task<IFleetSmtpProbeLease> AcquireAsync(SmtpThrottleContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IFleetSmtpProbeLease>(new FleetLease(true, null, null, cancellationToken));
}

internal sealed class FleetLease(bool acquired, string? reason, DateTimeOffset? retryAfter,
    CancellationToken token, CancellationTokenSource? deadline = null, Func<Task>? release = null) : IFleetSmtpProbeLease
{
    private int _disposed;
    public bool Acquired => acquired;
    public string? Reason => reason;
    public DateTimeOffset? RetryAfter => retryAfter;
    public CancellationToken ExecutionToken => token;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (release is not null) await release().ConfigureAwait(false); }
        finally { deadline?.Dispose(); }
    }
}

/// <summary>Non-waiting reservations against server time. Each scope has a bounded set of live owners.</summary>
public sealed class MongoFleetSmtpProbeBudget : IFleetSmtpProbeBudget, IDisposable
{
    private static readonly Meter Meter = new("EmailValidation.FleetProbeBudget");
    private static readonly Counter<long> Reservations = Meter.CreateCounter<long>("smtp_fleet_reservation_total");
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly FleetProbeBudgetOptions _options;
    private readonly int _sessionSeconds;
    private readonly IProviderPolicyResolver _providers;
    private readonly SemaphoreSlim _initialize = new(1, 1);
    private bool _initialized;
    public MongoFleetSmtpProbeBudget(IMongoClient client, IOptions<EmailValidationOptions> options, IProviderPolicyResolver providers)
    {
        _options = options.Value.Smtp.FleetBudget;
        _sessionSeconds = options.Value.Smtp.SessionTimeoutSeconds;
        _providers = providers;
        _collection = client.GetDatabase(options.Value.Persistence.DatabaseName).GetCollection<BsonDocument>(_options.Collection);
    }

    public async Task<IFleetSmtpProbeLease> AcquireAsync(SmtpThrottleContext context, CancellationToken cancellationToken = default)
    {
        if (_options.Mode == FleetProbeBudgetMode.Disabled)
            return new FleetLease(true, null, null, cancellationToken);
        var owner = Guid.NewGuid().ToString("N");
        var scopes = new[]
        {
            ("domain:" + context.Domain.Trim().TrimEnd('.').ToLowerInvariant(), _options.PerDomainConcurrency),
            ("provider:" + _providers.Resolve(context.Provider).ProviderKey, _options.PerProviderConcurrency),
            ("global", _options.GlobalConcurrency)
        };
        var acquired = new List<string>();
        // Starts BEFORE database acquisition. The client deadline precedes every server lease expiry,
        // independently of host clock skew. A suspended process still cannot retract a sent SMTP command.
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_sessionSeconds + 5));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.StoreTimeoutSeconds));
        string? failure = null;
        try
        {
            await InitializeAsync(timeout.Token).ConfigureAwait(false);
            foreach (var (scope, capacity) in scopes)
            {
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
                if (!await ReserveAsync(id, owner, capacity, timeout.Token).ConfigureAwait(false))
                {
                    failure = "FleetProbeCapacity";
                    break;
                }
                acquired.Add(id);
            }
        }
        catch (Exception exception) when (exception is MongoException or TimeoutException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { failure = "FleetProbeStoreUnavailable"; }
        catch
        {
            await ReleaseAsync(acquired, owner).ConfigureAwait(false);
            deadline.Dispose();
            throw;
        }
        Reservations.Add(1, new KeyValuePair<string, object?>("mode", _options.Mode.ToString()),
            new KeyValuePair<string, object?>("outcome", failure ?? "acquired"));
        if (failure is not null)
        {
            await ReleaseAsync(acquired, owner).ConfigureAwait(false);
            deadline.Dispose();
            return new FleetLease(_options.Mode == FleetProbeBudgetMode.Observe, failure,
                DateTimeOffset.UtcNow.AddSeconds(_options.RetrySeconds), cancellationToken);
        }
        return new FleetLease(true, null, null, deadline.Token, deadline, () => ReleaseAsync(acquired, owner));
    }

    private async Task<bool> ReserveAsync(string id, string owner, int capacity, CancellationToken token)
    {
        var live = new BsonDocument("$filter", new BsonDocument
        {
            { "input", new BsonDocument("$ifNull", new BsonArray { "$Holders", new BsonArray() }) },
            { "as", "holder" }, { "cond", new BsonDocument("$gt", new BsonArray { "$$holder.ExpiresAt", "$$NOW" }) }
        });
        var configuration = $"v1:{_sessionSeconds}:{_options.GlobalConcurrency}:{_options.PerProviderConcurrency}:{_options.PerDomainConcurrency}";
        var filter = new BsonDocument
        {
            { "_id", id }, { "$expr", new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$lt", new BsonArray { new BsonDocument("$size", live), capacity }),
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$Configuration", configuration }), configuration })
            }) }
        };
        var expires = new BsonDocument("$add", new BsonArray { "$$NOW", (_sessionSeconds + 15L) * 1000 });
        PipelineDefinition<BsonDocument, BsonDocument> pipeline = new[]
        {
            new BsonDocument("$set", new BsonDocument
            {
                { "Configuration", configuration },
                { "Holders", new BsonDocument("$concatArrays", new BsonArray { live,
                    new BsonArray { new BsonDocument { { "Owner", owner }, { "ExpiresAt", expires } } } }) },
                { "ExpiresAt", expires }
            })
        };
        try
        {
            // $expr admission must be an update, not an upsert. Create the empty scope separately;
            // racing creators converge on its unique _id before competing for an owner slot.
            PipelineDefinition<BsonDocument, BsonDocument> initialize = new[]
            {
                new BsonDocument("$set", new BsonDocument
                {
                    { "Configuration", new BsonDocument("$ifNull", new BsonArray { "$Configuration", configuration }) },
                    { "Holders", new BsonDocument("$ifNull", new BsonArray { "$Holders", new BsonArray() }) },
                    { "ExpiresAt", new BsonDocument("$ifNull", new BsonArray { "$ExpiresAt", expires }) }
                })
            };
            try
            {
                await _collection.UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Pipeline(initialize),
                    new UpdateOptions { IsUpsert = true }, token).ConfigureAwait(false);
            }
            catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey) { }
            var result = await _collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Pipeline(pipeline),
                cancellationToken: token).ConfigureAwait(false);
            return result.ModifiedCount == 1;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey) { return false; }
    }

    private async Task ReleaseAsync(List<string> ids, string owner)
    {
        if (ids.Count == 0) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.StoreTimeoutSeconds));
        try
        {
            await _collection.UpdateManyAsync(Builders<BsonDocument>.Filter.In("_id", ids),
                new BsonDocument("$pull", new BsonDocument("Holders", new BsonDocument("Owner", owner))),
                cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MongoException or TimeoutException or OperationCanceledException)
        { Reservations.Add(1, new KeyValuePair<string, object?>("outcome", "release_failed_expiry_recovers")); }
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        if (_initialized) return;
        await _initialize.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await _collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAt"),
                new CreateIndexOptions { Name = "ttl_probe_scopes", ExpireAfter = TimeSpan.Zero }), cancellationToken: token).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _initialize.Release(); }
    }
    public void Dispose() => _initialize.Dispose();
}
