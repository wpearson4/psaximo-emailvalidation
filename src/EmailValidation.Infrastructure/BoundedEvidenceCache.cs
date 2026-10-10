namespace EmailValidation.Infrastructure;

/// <summary>Absolute expiry and one linked-list node per key, including under replacement/remove churn.</summary>
internal sealed class BoundedEvidenceCache<T>(int capacity, TimeProvider clock) where T : class
{
    private sealed record Entry(T Value, DateTimeOffset ExpiresAt, LinkedListNode<string> Node);
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    public int Count { get { lock (_sync) return _entries.Count; } }
    internal int BookkeepingCount { get { lock (_sync) return _order.Count; } }
    public bool TryGet(string key, out T? value)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > clock.GetUtcNow()) { value = entry.Value; return true; }
                RemoveCore(key);
            }
            value = null;
            return false;
        }
    }
    public void Set(string key, T value, DateTimeOffset expiresAt)
    {
        lock (_sync)
        {
            RemoveCore(key);
            if (expiresAt <= clock.GetUtcNow() || capacity <= 0) return;
            while (_entries.Count >= capacity) RemoveCore(_order.First!.Value);
            _entries.Add(key, new(value, expiresAt, _order.AddLast(key)));
        }
    }
    public void Clear() { lock (_sync) { _entries.Clear(); _order.Clear(); } }
    public void Remove(string key) { lock (_sync) RemoveCore(key); }
    private void RemoveCore(string key)
    {
        if (_entries.Remove(key, out var removed)) _order.Remove(removed.Node);
    }
}
