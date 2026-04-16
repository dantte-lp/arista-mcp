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
    public required float[] Embedding { get; init; }
    public string EmbeddingModel { get; init; } = "snowflake-arctic-embed-m-v1.5";
}
