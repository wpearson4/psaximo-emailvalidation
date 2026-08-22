using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

/// <summary>
/// Bounded process-local result cache. Entries use absolute policy-derived expiration;
/// the abstraction can be replaced by a distributed cache without changing orchestration.
/// </summary>
public sealed class InMemoryValidationResultCache(
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider) : IValidationResultCache
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<CacheToken> _insertionOrder = new();
    private readonly int _sizeLimit = Math.Max(1, options.Value.ResultReuse.MemoryCacheSizeLimit);
    private long _version;

    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    public Task<EmailValidationResult?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return Task.FromResult<EmailValidationResult?>(null);
            if (entry.ExpiresAt <= timeProvider.GetUtcNow())
            {
                _entries.Remove(key);
                return Task.FromResult<EmailValidationResult?>(null);
            }
            return Task.FromResult<EmailValidationResult?>(entry.Result);
        }
    }

    public Task SetAsync(
        string key,
        EmailValidationResult result,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (lifetime <= TimeSpan.Zero)
            {
                _entries.Remove(key);
                return Task.CompletedTask;
            }

            var version = ++_version;
            _entries[key] = new CacheEntry(result, timeProvider.GetUtcNow().Add(lifetime), version);
            _insertionOrder.Enqueue(new CacheToken(key, version));
            TrimToLimit();
            return Task.CompletedTask;
        }
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) _entries.Remove(key);
        return Task.CompletedTask;
    }

    private void TrimToLimit()
    {
        while (_entries.Count > _sizeLimit && _insertionOrder.TryDequeue(out var token))
        {
            if (_entries.TryGetValue(token.Key, out var entry) && entry.Version == token.Version)
                _entries.Remove(token.Key);
        }
    }

    private sealed record CacheEntry(EmailValidationResult Result, DateTimeOffset ExpiresAt, long Version);
    private sealed record CacheToken(string Key, long Version);
}
