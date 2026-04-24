namespace AristaMcp.Core.Retrieval;

// Expands a user query into a hypothetical answer paragraph suitable for
// use as a dense-retrieval query (HyDE pattern). Implementations must
// degrade gracefully — on LLM timeout, error, or disabled state they
// return the original query unchanged, never throw. Rerankers and BM25
// continue to operate on the raw query.
public interface IHydeExpander
{
    Task<HydeResult> ExpandAsync(string query, CancellationToken ct);
}
