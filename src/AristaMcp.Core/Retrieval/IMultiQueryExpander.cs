namespace AristaMcp.Core.Retrieval;

// Produces additional dense-retrieval query variants from a single
// QueryExpander-annotated input. Cheap rule-based transforms only — no
// LLM, no hallucination, no latency cost beyond extra embedding calls
// which the QueryEmbeddingCache absorbs after warm-up.
//
// BM25 and the reranker continue to operate on the canonical expanded
// query; multi-query variants only widen the dense candidate pool.
public interface IMultiQueryExpander
{
    // First entry is always the input itself so callers can iterate
    // unconditionally. De-duplicated, ordered, never empty.
    IReadOnlyList<string> Expand(string expandedQuery);
}
