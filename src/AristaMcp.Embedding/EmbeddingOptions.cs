namespace AristaMcp.Embedding;

public sealed class EmbeddingOptions
{
    public required string ModelPath { get; init; }
    public required string VocabPath { get; init; }
    public int MaxSequenceLength { get; init; } = 512;
    public int BatchSize { get; init; } = 16;
    public bool Gpu { get; init; }

    // Prefix mandated by the snowflake-arctic-embed-m-v1.5 model card for queries.
    // Documents are embedded without modification.
    public string QueryPrefix { get; init; } = "Represent this sentence for searching relevant passages: ";
}
