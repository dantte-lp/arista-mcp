namespace AristaMcp.Core.Retrieval;

// Production fallback when no reranker model is configured. Passes candidates through
// with descending synthetic scores so the original RRF order is preserved.
public sealed class NoopReranker : IReranker
{
    public Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var result = new RerankResult[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            result[i] = new RerankResult(candidates[i].ChunkId, candidates.Count - i);
        }

        return Task.FromResult<IReadOnlyList<RerankResult>>(result);
    }

    public void Dispose() { }
}
