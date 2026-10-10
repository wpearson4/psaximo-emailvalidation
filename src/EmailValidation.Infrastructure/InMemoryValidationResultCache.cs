using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class InMemoryValidationResultCache(
    IOptions<EmailValidationOptions> options, TimeProvider timeProvider) : IValidationResultCache
{
    private readonly BoundedEvidenceCache<EmailValidationResult> _entries = new(options.Value.ResultReuse.MemoryCacheSizeLimit, timeProvider);
    public int Count => _entries.Count;
    internal int BookkeepingCount => _entries.BookkeepingCount;
    public Task<EmailValidationResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.TryGet(key, out var result);
        return Task.FromResult(result);
    }
    public Task SetAsync(string key, EmailValidationResult result, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.Set(key, result, timeProvider.GetUtcNow().Add(lifetime));
        return Task.CompletedTask;
    }
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.Remove(key);
        return Task.CompletedTask;
    }
}
