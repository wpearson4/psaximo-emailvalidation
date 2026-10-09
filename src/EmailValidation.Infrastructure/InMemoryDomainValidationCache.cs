using System.Collections.Concurrent;
using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

public sealed class InMemoryDomainValidationCache(TimeProvider? timeProvider = null) : IDomainValidationCache
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private sealed record CacheItem(DomainIntelligence Data, DateTimeOffset ExpiresUtc);
    private readonly ConcurrentDictionary<string, CacheItem> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    public bool TryGet(string domain, out DomainIntelligence? data)
    {
        data = null;
        if (!_entries.TryGetValue(domain, out var item)) return false;
        if (item.ExpiresUtc <= _clock.GetUtcNow())
        {
            _entries.TryRemove(domain, out _);
            return false;
        }
        data = item.Data;
        return true;
    }

    public void Store(DomainIntelligence data, TimeSpan lifetime)
    {
        var expiresAt = _clock.GetUtcNow().Add(lifetime);
        if (data.EvidenceExpiresAt is { } observedExpiry && observedExpiry < expiresAt) expiresAt = observedExpiry;
        _entries[data.Domain] = new CacheItem(data, expiresAt);
    }
}
