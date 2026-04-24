namespace AristaMcp.Embedding;

public sealed class RerankerOptions
{
    public required string ModelPath { get; init; }

    // Tokenizer asset path. Holds the WordPiece vocab file path for
    // OnnxReranker, or the SentencePiece BPE model path for
    // XlmRobertaOnnxReranker. Named VocabPath for historical compatibility.
    public required string VocabPath { get; init; }

    // Query+doc pair is truncated to this total length (including special tokens).
    public int MaxSequenceLength { get; init; } = 512;

    // Cross-encoders are heavier per token than bi-encoders; keep the batch smaller.
    public int BatchSize { get; init; } = 8;

    public bool Gpu { get; init; }
}
