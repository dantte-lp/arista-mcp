using System.ComponentModel.DataAnnotations.Schema;
using Pgvector;

namespace AristaMcp.Data.Entities;

[Table("chunks")]
public class ChunkEntity
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

    [Column("embedding", TypeName = "halfvec(768)")]
    public HalfVector Embedding { get; set; } = null!;

    [Column("embedding_model")] public string EmbeddingModel { get; set; } = "snowflake-arctic-embed-m-v1.5";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }

    public DocumentEntity Document { get; set; } = null!;
}
