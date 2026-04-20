using System.Text.Json.Serialization;

namespace AristaMcp.Core.Catalog;

// Mirrors one entry in arista-docs/data/catalog.json (documents array).
public sealed record CatalogEntry
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("url")] public required string Url { get; init; }
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("product")] public string? Product { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("slug")] public required string Slug { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("pdf_path")] public string? PdfPath { get; init; }
    [JsonPropertyName("md_path")] public required string MdPath { get; init; }
    [JsonPropertyName("json_path")] public required string JsonPath { get; init; }
    [JsonPropertyName("pdf_sha256")] public string? PdfSha256 { get; init; }
    [JsonPropertyName("pages")] public int? Pages { get; init; }
    [JsonPropertyName("size_bytes")] public long? SizeBytes { get; init; }
    [JsonPropertyName("convert_mode")] public string? ConvertMode { get; init; }
    [JsonPropertyName("image_count")] public int ImageCount { get; init; }
    [JsonPropertyName("section_count")] public int SectionCount { get; init; }
    [JsonPropertyName("level1_section_count")] public int Level1SectionCount { get; init; }
    [JsonPropertyName("toc_count")] public int TocCount { get; init; }
    [JsonPropertyName("downloaded_at")] public DateTimeOffset? DownloadedAt { get; init; }
    [JsonPropertyName("converted_at")] public DateTimeOffset? ConvertedAt { get; init; }
}
