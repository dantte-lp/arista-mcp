using System.ComponentModel.DataAnnotations.Schema;

namespace AristaMcp.Data.Entities;

[Table("documents")]
public sealed class DocumentEntity
{
    [Column("id")] public string Id { get; set; } = "";
    [Column("url")] public string Url { get; set; } = "";
    [Column("category")] public string Category { get; set; } = "";
    [Column("product")] public string? Product { get; set; }
    [Column("version")] public string? Version { get; set; }
    [Column("title")] public string Title { get; set; } = "";
    [Column("slug")] public string Slug { get; set; } = "";

    [Column("tags", TypeName = "jsonb")]
    public string TagsJson { get; set; } = "[]";

    [Column("pages")] public int? Pages { get; set; }
    [Column("size_bytes")] public long? SizeBytes { get; set; }
    [Column("pdf_sha256")] public string? PdfSha256 { get; set; }
    [Column("md_path")] public string MdPath { get; set; } = "";
    [Column("json_path")] public string JsonPath { get; set; } = "";
    [Column("convert_mode")] public string? ConvertMode { get; set; }
    [Column("image_count")] public int ImageCount { get; set; }
    [Column("section_count")] public int SectionCount { get; set; }
    [Column("level1_section_count")] public int Level1SectionCount { get; set; }
    [Column("toc_count")] public int TocCount { get; set; }
    [Column("downloaded_at")] public DateTimeOffset? DownloadedAt { get; set; }
    [Column("converted_at")] public DateTimeOffset? ConvertedAt { get; set; }
    [Column("ingested_at")] public DateTimeOffset IngestedAt { get; set; }
}
