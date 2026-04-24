namespace AristaMcp.Core.Retrieval;

// Default IHydeExpander implementation when HyDE is disabled or the LLM
// sidecar is absent. Returns the query unchanged with zero latency.
public sealed class NoopHydeExpander : IHydeExpander
{
    public Task<HydeResult> ExpandAsync(string query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(new HydeResult(
            DenseQuery: query,
            LatencyMs: 0,
            CacheHit: false,
            UsedFallback: true));
    }
}
