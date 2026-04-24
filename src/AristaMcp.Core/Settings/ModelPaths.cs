namespace AristaMcp.Core.Settings;

// Centralises filesystem layout for the ONNX models and applies the
// EmbeddingVariant toggle. fp16 points at Snowflake's shipped
// `model_fp16.onnx`; fp32 at the default `model.onnx`. Caller is responsible
// for supplying the actual file — this helper only builds the path.
public static class ModelPaths
{
    public static string EmbedderModel(AristaMcpSettings settings) =>
        Path.Combine(settings.ModelsDir, "embedder",
            string.Equals(settings.EmbeddingVariant, "fp16", StringComparison.OrdinalIgnoreCase)
                ? "model_fp16.onnx"
                : "model.onnx");

    public static string EmbedderVocab(AristaMcpSettings settings) =>
        Path.Combine(settings.ModelsDir, "embedder", "vocab.txt");

    public static string RerankerModel(string modelsDir) =>
        Path.Combine(modelsDir, "reranker", "model.onnx");

    public static string RerankerVocab(string modelsDir) =>
        Path.Combine(modelsDir, "reranker", "vocab.txt");

    // SentencePiece BPE model used by XLM-RoBERTa / bge-reranker-* cross-encoders.
    public static string RerankerSpm(string modelsDir) =>
        Path.Combine(modelsDir, "reranker", "sentencepiece.bpe.model");
}
