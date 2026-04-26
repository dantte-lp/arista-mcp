namespace AristaMcp.Core.Retrieval;

// Listwise re-rank pass on the cross-encoder's top-N. The implementation
// receives N candidates (ordered by upstream rerank score) and returns a
// permutation of their chunk ids in MOST to LEAST relevant order. On any
// failure (timeout, malformed output, circuit open) it must return the
// candidates' chunk ids in the input order so the retriever can blindly
// concatenate without checking.
public interface IListwiseReranker
{
    // Number of cross-encoder candidates the implementation will accept
    // for listwise reordering. Caller slices its reranked list to this
    // size before invoking ReorderAsync; positions beyond stay in
    // upstream order. Zero means listwise is disabled (caller skips).
    int MaxCandidates { get; }

    Task<ListwiseResult> ReorderAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct);
}
