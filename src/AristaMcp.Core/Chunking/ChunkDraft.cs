namespace AristaMcp.Core.Chunking;

// Pre-embed chunk — has the document+section prefix applied (Content) plus the raw body
// (RawContent) for BM25 + display. Embedding is filled in by the ingest pipeline.
public sealed record ChunkDraft
{
    public required string Content { get; init; }
    public required string RawContent { get; init; }
    public required string SectionTitle { get; init; }
    public required short SectionLevel { get; init; }
    public int? PageStart { get; init; }
    public int? PageEnd { get; init; }
    public required int TokenCount { get; init; }
}
