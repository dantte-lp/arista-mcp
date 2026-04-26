namespace AristaMcp.Core.Chunking;

// Pre-embed chunk. Content carries the document+section prefix; RawContent
// is the raw body for BM25 + display. Embedding is filled in by the ingest
// pipeline (Leaf only — Parent rows skip the embedder).
public sealed record ChunkDraft
{
    public required string Content { get; init; }
    public required string RawContent { get; init; }
    public required string SectionTitle { get; init; }
    public required short SectionLevel { get; init; }
    public int? PageStart { get; init; }
    public int? PageEnd { get; init; }
    public required int TokenCount { get; init; }

    // Sprint 15: parent-child chunking.
    public ChunkKind Kind { get; init; } = ChunkKind.Leaf;

    // Index into the parent list emitted by the chunker — resolved to a
    // real parent_chunk_id (DB long) at insert time once parents are
    // persisted. Null on parent rows and on legacy single-pass output.
    public int? ParentIndex { get; init; }
}
