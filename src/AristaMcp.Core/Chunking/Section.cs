namespace AristaMcp.Core.Chunking;

public sealed record Section
{
    public required string Title { get; init; }
    public required short Level { get; init; }
    public required string Content { get; init; }
    public int? PageStart { get; init; }
    public int? PageEnd { get; init; }
}
