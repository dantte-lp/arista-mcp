using System.Collections.Concurrent;
using Pgvector;

namespace AristaMcp.Server.Retrieval;

// Bounded cache of query → HalfVector embeddings. Claude-driven search flows
// often repeat near-identical queries (e.g. "EVPN" then "EVPN configuration"
// collapses to the same normalized string after QueryExpander); caching the
// first embedding saves a ~100-200 ms ONNX inference on every subsequent hit.
//
// Eviction is "clear oldest half when full", not strict LRU — a perf cache
// shouldn't justify a linked-list + lock overhead. Under steady-state use this
// converges to a workable hot-set size. 256 entries at 1.5 KB each (768 × 2 B
// HalfVector) ≈ 400 KB ceiling, negligible.
public sealed class QueryEmbeddingCache
{
    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private long _accessCounter;

    public QueryEmbeddingCache(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public bool TryGet(string key, out HalfVector value)
    {
        if (_map.TryGetValue(key, out var entry))
        {
            entry.LastAccess = Interlocked.Increment(ref _accessCounter);
            value = entry.Vector;
            return true;
        }
        value = default!;
        return false;
    }

    public void Add(string key, HalfVector value)
    {
        _map[key] = new Entry
        {
            Vector = value,
            LastAccess = Interlocked.Increment(ref _accessCounter),
        };

        if (_map.Count > _capacity)
        {
            EvictOldestHalf();
        }
    }

    private void EvictOldestHalf()
    {
        // Snapshot LastAccess values, sort, and drop the oldest half. Under
        // churn, this caps the working set at ~capacity.
        var snapshot = _map.ToArray();
        Array.Sort(snapshot, static (a, b) => a.Value.LastAccess.CompareTo(b.Value.LastAccess));
        var evictCount = snapshot.Length - (_capacity / 2);
        for (var i = 0; i < evictCount; i++)
        {
            _map.TryRemove(snapshot[i].Key, out _);
        }
    }

    private sealed class Entry
    {
        public required HalfVector Vector { get; init; }
        public long LastAccess { get; set; }
    }
}
