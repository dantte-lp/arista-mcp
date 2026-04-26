namespace AristaMcp.Core.Retrieval;

// Default IListwiseReranker when listwise re-rank is disabled. Returns
// the input order with zero latency and UsedFallback=true.
public sealed class NoopListwiseReranker : IListwiseReranker
{
    // Zero signals the caller to skip the listwise step entirely.
    public int MaxCandidates => 0;

    public Task<ListwiseResult> ReorderAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);
        var ids = new long[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            ids[i] = candidates[i].ChunkId;
        }
        return Task.FromResult(new ListwiseResult(ids, 0, false, true));
    }
}
