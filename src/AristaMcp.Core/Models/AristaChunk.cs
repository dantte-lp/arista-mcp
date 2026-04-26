using AristaMcp.Core.Chunking;

namespace AristaMcp.Core.Models;

public sealed record AristaChunk
{
    public long Id { get; init; }
    public required string DocumentId { get; init; }
    public required int ChunkIndex { get; init; }
    public required string Content { get; init; }
    public required string RawContent { get; init; }
    public string? SectionTitle { get; init; }
    public short? SectionLevel { get; init; }
    public int? PageStart { get; init; }
    public int? PageEnd { get; init; }
    public required int TokenCount { get; init; }

    // Sprint 15: parent-child chunking. Embedding is nullable because parent
    // rows are not embedded — the dense path filters on the leaf kind.
    // ParentChunkId is null on parent rows and populated on leaves after
    // the first ingest pass writes parents and the second pass patches in
    // the FK before the leaf bulk-insert.
    public ChunkKind ChunkKind { get; init; } = ChunkKind.Leaf;
    public long? ParentChunkId { get; init; }
    public float[]? Embedding { get; init; }
    public string? EmbeddingModel { get; init; } = "snowflake-arctic-embed-m-v1.5";
}
