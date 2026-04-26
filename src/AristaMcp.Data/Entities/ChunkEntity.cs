using System.ComponentModel.DataAnnotations.Schema;
using Pgvector;

namespace AristaMcp.Data.Entities;

[Table("chunks")]
public sealed class ChunkEntity
{
    [Column("id")] public long Id { get; set; }
    [Column("document_id")] public string DocumentId { get; set; } = "";
    [Column("chunk_index")] public int ChunkIndex { get; set; }
    [Column("content")] public string Content { get; set; } = "";
    [Column("raw_content")] public string RawContent { get; set; } = "";
    [Column("section_title")] public string? SectionTitle { get; set; }
    [Column("section_level")] public short? SectionLevel { get; set; }
    [Column("page_start")] public int? PageStart { get; set; }
    [Column("page_end")] public int? PageEnd { get; set; }
    [Column("token_count")] public int TokenCount { get; set; }

    // Sprint 15: parent-child chunking. Parents are full-section text up to
    // about two thousand tokens, emitted alongside their leaf slices.
    // Parent rows carry the kind 'parent' and have Embedding null — they
    // are never embedded; they exist only to provide richer context to
    // the cross-encoder reranker during the hydration step. Leaves point
    // at their parent via ParentChunkId; legacy rows ingested before
    // v0.2.5 have ParentChunkId null and the retriever falls back to
    // leaf content for those.
    [Column("parent_chunk_id")] public long? ParentChunkId { get; set; }
    [Column("chunk_kind")] public string ChunkKind { get; set; } = "leaf";

    // Embedding is nullable because parent chunks are not embedded.
    // Retrieval queries filter on the 'leaf' kind so the HNSW index never
    // sees the NULL rows.
    [Column("embedding", TypeName = "halfvec(768)")]
    public HalfVector? Embedding { get; set; }

    [Column("embedding_model")] public string? EmbeddingModel { get; set; } = "snowflake-arctic-embed-m-v1.5";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }

    public DocumentEntity Document { get; set; } = null!;

    // Self-FK navigation for the parent. Optional — the retriever hydrates
    // parent content via a single batch SELECT rather than EF navigation;
    // this property exists for migration generation and any future EF-side
    // join scenarios.
    public ChunkEntity? Parent { get; set; }
}
