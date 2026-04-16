namespace AristaMcp.Core.Models;

public sealed record AristaDocument
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string Category { get; init; }
    public string? Product { get; init; }
    public string? Version { get; init; }
    public required string Title { get; init; }
    public required string Slug { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public int? Pages { get; init; }
    public long? SizeBytes { get; init; }
    public string? PdfSha256 { get; init; }
    public required string MdPath { get; init; }
    public required string JsonPath { get; init; }
    public string? ConvertMode { get; init; }
    public int ImageCount { get; init; }
    public int SectionCount { get; init; }
    public int Level1SectionCount { get; init; }
    public int TocCount { get; init; }
    public DateTimeOffset? DownloadedAt { get; init; }
    public DateTimeOffset? ConvertedAt { get; init; }
}
