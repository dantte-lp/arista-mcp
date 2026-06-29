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

    // Effective reranker directory — honours the `RerankerDir` override on
    // AristaMcpSettings when set (production INT8 fine-tune path), falls back
    // to `{ModelsDir}/reranker` otherwise. The string-taking overloads below
    // remain for callers that pre-resolve the directory; new code should
    // prefer the settings-taking overload so `RerankerDir` is honoured
    // uniformly.
    public static string RerankerDir(AristaMcpSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.RerankerDir)
            ? settings.RerankerDir
            : Path.Combine(settings.ModelsDir, "reranker");

    public static string RerankerModel(string rerankerDir) =>
        Path.Combine(rerankerDir, "model.onnx");

    public static string RerankerVocab(string rerankerDir) =>
        Path.Combine(rerankerDir, "vocab.txt");

    // SentencePiece BPE model used by XLM-RoBERTa / bge-reranker-* cross-encoders.
    public static string RerankerSpm(string rerankerDir) =>
        Path.Combine(rerankerDir, "sentencepiece.bpe.model");
}
