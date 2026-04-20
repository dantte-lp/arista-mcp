namespace AristaMcp.Core.Chunking;

public sealed class ChunkingOptions
{
    public int TargetTokens { get; init; } = 512;
    public int MaxTokens { get; init; } = 1200;
    public int OverlapTokens { get; init; } = 64;
    public int MinTokens { get; init; } = 40;
}
