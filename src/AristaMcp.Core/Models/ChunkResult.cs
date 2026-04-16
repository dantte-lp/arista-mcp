namespace AristaMcp.Core.Models;

public sealed record ChunkResult(
    long ChunkId,
    string DocumentId,
    string DocumentTitle,
    string DocumentSlug,
    string Category,
    string? Product,
    string? Version,
    string? SectionTitle,
    short? SectionLevel,
    int? PageStart,
    int? PageEnd,
    string RawContent,
    float Score,
    float? DenseSimilarity,
    float? Bm25Score,
    float? RrfScore,
    float? RerankScore);
