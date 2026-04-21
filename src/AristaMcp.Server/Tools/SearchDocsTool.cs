using System.ComponentModel;
using AristaMcp.Server.Retrieval;
using ModelContextProtocol.Server;

namespace AristaMcp.Server.Tools;

[McpServerToolType]
public sealed class SearchDocsTool(IHybridRetriever retriever)
{
    [McpServerTool(Name = "search_docs")]
    [Description("Hybrid search (dense + BM25 + rerank) across Arista documentation. Returns ranked chunks with doc metadata and section info.")]
    public async Task<object> SearchAsync(
        [Description("Natural-language query, e.g. 'EVPN overlay configuration'")] string query,
        [Description("Max results (1-50). Defaults to 10.")] int topK = 10,
        [Description("Optional category filter ('toi' or 'manual').")] string? category = null,
        [Description("Optional product filter.")] string? product = null,
        [Description("Include per-stage diagnostics (dense/sparse counts, timings).")] bool withDiagnostics = false,
        [Description("Drop duplicate chunks from the same document+section, keeping only the top-scoring.")] bool dedupPerSection = false,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(topK, 1, 50);
        var opts = new RetrievalOptions
        {
            Limit = limit,
            CandidatePoolSize = Math.Max(50, limit * 5),
            RerankTopN = Math.Max(30, limit * 3),
            Category = category,
            Product = product,
            DedupPerSection = dedupPerSection,
        };

        var response = await retriever.SearchAsync(query, opts, ct).ConfigureAwait(false);

        var results = response.Results.Select(r => new
        {
            chunk_id = r.ChunkId,
            document_id = r.DocumentId,
            document_title = r.DocumentTitle,
            document_slug = r.DocumentSlug,
            category = r.Category,
            product = r.Product,
            version = r.Version,
            section_title = r.SectionTitle,
            page_start = r.PageStart,
            page_end = r.PageEnd,
            score = r.Score,
            content = r.RawContent,
        }).ToList();

        if (withDiagnostics)
        {
            return new { results, diagnostics = response.Diagnostics };
        }

        return new { results };
    }
}
