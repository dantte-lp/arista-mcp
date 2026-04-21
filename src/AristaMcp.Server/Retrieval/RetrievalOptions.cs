namespace AristaMcp.Server.Retrieval;

public sealed class RetrievalOptions
{
    // Number of candidates to pull from each of dense + sparse before fusion.
    public int CandidatePoolSize { get; init; } = 50;

    // RRF k constant — higher reduces the influence of rank differences between sides.
    public int RrfK { get; init; } = 60;

    // How many top RRF-fused candidates to send to the reranker.
    public int RerankTopN { get; init; } = 30;

    // Optional category filter applied at SQL level.
    public string? Category { get; init; }

    // Optional product filter applied at SQL level.
    public string? Product { get; init; }

    // Final number of hits returned to the caller.
    public int Limit { get; init; } = 10;

    // When true, drop all but the top-scoring chunk per (document_id, section_title)
    // after reranking — avoids redundant near-duplicate chunks from long sections
    // dominating the result set.
    public bool DedupPerSection { get; init; }
}
