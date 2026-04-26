namespace AristaMcp.Core.Chunking;

public sealed class ChunkingOptions
{
    public int TargetTokens { get; init; } = 512;
    public int MaxTokens { get; init; } = 1200;
    public int OverlapTokens { get; init; } = 64;
    public int MinTokens { get; init; } = 40;

    // Sprint 15: parent chunk size budget. Parents hold the full surrounding
    // section text for the reranker to score; truncated to head + tail when
    // a section blows past the budget so the reranker still sees both ends.
    public int ParentMaxTokens { get; init; } = 2048;
    public int ParentHeadTokens { get; init; } = 1792;
    public int ParentTailTokens { get; init; } = 256;
}
