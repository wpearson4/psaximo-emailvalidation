using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class InMemoryDomainValidationCache(TimeProvider? timeProvider = null, IOptions<EmailValidationOptions>? options = null) : IDomainValidationCache
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly BoundedEvidenceCache<DomainIntelligence> _entries = new(
        options?.Value.Persistence.EvidenceCacheSizeLimit ?? 10_000, timeProvider ?? TimeProvider.System);
    private readonly object _sync = new();
    public int Count => _entries.Count;
    public bool TryGet(string domain, out DomainIntelligence? data) => _entries.TryGet(domain.ToLowerInvariant(), out data);
    public void Store(DomainIntelligence data, TimeSpan lifetime) => StoreMergedAsync(data, lifetime).GetAwaiter().GetResult();
    public Task<DomainIntelligence> StoreMergedAsync(DomainIntelligence data, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            TryGet(data.Domain, out var current);
            var merged = current is null ? data : DomainIntelligenceMerge.Merge(current, data);
            var expires = _clock.GetUtcNow().Add(lifetime);
            if (merged.EvidenceExpiresAt is { } observed && observed < expires) expires = observed;
            _entries.Set(data.Domain.ToLowerInvariant(), merged, expires);
            return Task.FromResult(merged);
        }
    }
}
