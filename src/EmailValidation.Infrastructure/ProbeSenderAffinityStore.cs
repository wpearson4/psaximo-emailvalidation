using System.Collections.Concurrent;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class ProbeSenderAffinityStore(
    TimeProvider timeProvider,
    IOptions<EmailValidationOptions> options) : IProbeSenderAffinityStore
{
    private readonly ConcurrentDictionary<string, ProbeSenderAffinity> _affinities =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _incompatible =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ProbeSenderRotationOptions _options = options.Value.ProbeSenderRotation;
    private long _created;
    private long _retained;
    private long _changed;
    private long _removed;
    private long _compatibilityRejections;

    public ProbeSenderAffinity? GetAffinity(string recipientDomain)
    {
        if (!_affinities.TryGetValue(recipientDomain, out var affinity)) return null;
        if (affinity.ExpiresAt > timeProvider.GetUtcNow())
        {
            Interlocked.Increment(ref _retained);
            return affinity;
        }
        _affinities.TryRemove(recipientDomain, out _);
        Interlocked.Increment(ref _removed);
        return null;
    }

    public void SetAffinity(string recipientDomain, string sender)
    {
        var now = timeProvider.GetUtcNow();
        var replacement = new ProbeSenderAffinity(
            recipientDomain, sender, now,
            now.AddMinutes(Math.Max(1, _options.SenderAffinityMinutes)));
        _affinities.AddOrUpdate(
            recipientDomain,
            _ =>
            {
                Interlocked.Increment(ref _created);
                return replacement;
            },
            (_, existing) =>
            {
                if (!string.Equals(existing.Sender, sender, StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref _changed);
                return replacement;
            });
    }

    public void Remove(string recipientDomain)
    {
        if (_affinities.TryRemove(recipientDomain, out _)) Interlocked.Increment(ref _removed);
    }

    public void RemoveSender(string sender)
    {
        foreach (var affinity in _affinities.Where(pair =>
                     string.Equals(pair.Value.Sender, sender, StringComparison.OrdinalIgnoreCase)))
            if (_affinities.TryRemove(affinity.Key, out _)) Interlocked.Increment(ref _removed);
    }

    public void MarkIncompatible(string recipientDomain, string sender)
    {
        var key = CompatibilityKey(recipientDomain, sender);
        _incompatible[key] = timeProvider.GetUtcNow().AddMinutes(
            Math.Max(1, _options.SenderCompatibilityMinutes));
        Interlocked.Increment(ref _compatibilityRejections);
    }

    public ProbeSenderAffinitySnapshot GetSnapshot() => new(
        Count,
        Interlocked.Read(ref _created),
        Interlocked.Read(ref _retained),
        Interlocked.Read(ref _changed),
        Interlocked.Read(ref _removed),
        Interlocked.Read(ref _compatibilityRejections));

    public IReadOnlySet<string> GetIncompatibleSenders(string recipientDomain)
    {
        var now = timeProvider.GetUtcNow();
        var prefix = recipientDomain + "\n";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _incompatible)
        {
            if (pair.Value <= now)
            {
                _incompatible.TryRemove(pair.Key, out _);
                continue;
            }
            if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                result.Add(pair.Key[prefix.Length..]);
        }
        return result;
    }

    public int Count
    {
        get
        {
            var now = timeProvider.GetUtcNow();
            foreach (var affinity in _affinities.Where(pair => pair.Value.ExpiresAt <= now))
            {
                if (_affinities.TryRemove(affinity.Key, out _)) Interlocked.Increment(ref _removed);
            }
            return _affinities.Count;
        }
    }

    private static string CompatibilityKey(string domain, string sender) => domain + "\n" + sender;
}
