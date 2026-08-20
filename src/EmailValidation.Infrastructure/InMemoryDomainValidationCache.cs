using System.Collections.Concurrent;
using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

public sealed class InMemoryDomainValidationCache : IDomainValidationCache
{
    private sealed record CacheItem(DomainIntelligence Data, DateTimeOffset ExpiresUtc);
    private readonly ConcurrentDictionary<string, CacheItem> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    public bool TryGet(string domain, out DomainIntelligence? data)
    {
        data = null;
        if (!_entries.TryGetValue(domain, out var item)) return false;
        if (item.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(domain, out _);
            return false;
        }
        data = item.Data;
        return true;
    }

    public void Store(DomainIntelligence data, TimeSpan lifetime) =>
        _entries[data.Domain] = new CacheItem(data, DateTimeOffset.UtcNow.Add(lifetime));
}
