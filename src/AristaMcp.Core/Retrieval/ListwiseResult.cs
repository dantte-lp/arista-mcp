namespace AristaMcp.Core.Retrieval;

// Output of IListwiseReranker.ReorderAsync. OrderedChunkIds is a
// permutation of the input candidates' ChunkIds; LatencyMs and CacheHit
// help the retriever populate diagnostics without a second clock; on
// failure UsedFallback is true and OrderedChunkIds matches the input
// order verbatim.
public readonly record struct ListwiseResult(
    IReadOnlyList<long> OrderedChunkIds,
    double LatencyMs,
    bool CacheHit,
    bool UsedFallback);
