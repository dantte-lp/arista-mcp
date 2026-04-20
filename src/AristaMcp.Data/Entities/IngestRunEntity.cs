using System.ComponentModel.DataAnnotations.Schema;

namespace AristaMcp.Data.Entities;

[Table("ingest_runs")]
public sealed class IngestRunEntity
{
    [Column("id")] public long Id { get; set; }
    [Column("started_at")] public DateTimeOffset StartedAt { get; set; }
    [Column("finished_at")] public DateTimeOffset? FinishedAt { get; set; }
    [Column("status")] public string Status { get; set; } = "running";
    [Column("docs_total")] public int DocsTotal { get; set; }
    [Column("docs_skipped")] public int DocsSkipped { get; set; }
    [Column("docs_upserted")] public int DocsUpserted { get; set; }
    [Column("chunks_upserted")] public int ChunksUpserted { get; set; }
    [Column("catalog_sha256")] public string? CatalogSha256 { get; set; }
    [Column("error_msg")] public string? ErrorMsg { get; set; }
}
