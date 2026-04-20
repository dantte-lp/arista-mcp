namespace AristaMcp.Embedding;

public sealed class RerankerOptions
{
    public required string ModelPath { get; init; }
    public required string VocabPath { get; init; }

    // Each candidate gets tokenized as [CLS] query [SEP] doc [SEP], truncated to this.
    public int MaxSequenceLength { get; init; } = 512;

    // Cross-encoders are heavier per token than bi-encoders; keep the batch smaller.
    public int BatchSize { get; init; } = 8;

    public bool Gpu { get; init; }
}
