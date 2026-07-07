namespace AristaMcp.Core.Settings;

public sealed class AristaMcpSettings
{
    // Default points at the local podman postgres from docker/compose.yaml; production
    // deployments must override via ARISTA_MCP__ConnectionString.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "S2068:Credentials should not be hard-coded",
        Justification = "Local dev default only; overridden in all real deployments.")]
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista";
    public string ModelsDir { get; set; } = "models";

    // Override for the reranker model directory. Falls back to {ModelsDir}/reranker
    // when null. Production override for the v0.3.0 bge-reranker-v2-m3 INT8
    // fine-tune (or any side-by-side experimental reranker):
    //   ARISTA_MCP__RerankerDir=/var/lib/arista-mcp/models/reranker-v2m3-int8
    // See deploy/systemd/arista-mcp.env.example.
    public string? RerankerDir { get; set; }

    public string EmbeddingModel { get; set; } = "snowflake-arctic-embed-m-v1.5";
    public int EmbeddingDim { get; set; } = 768;

    // Embedder weight precision. "fp32" (default) uses `models/embedder/model.onnx`
    // (~436 MB). "fp16" uses `models/embedder/model_fp16.onnx` (~218 MB) for
    // 1.5-2x CPU speedup at <= 1 pp nDCG@10 cost per Snowflake's card. Set via
    // ARISTA_MCP__EmbeddingVariant=fp16 when latency matters.
    public string EmbeddingVariant { get; set; } = "fp32";

    public string RerankerModel { get; set; } = "bge-reranker-base";
    public bool Gpu { get; set; }
    public McpTransport Transport { get; set; } = McpTransport.Stdio;
    public int HttpPort { get; set; } = 8080;
    public int IngestBatchSize { get; set; } = 32;
    public int ChunkMaxTokens { get; set; } = 1200;
    public int ChunkTargetTokens { get; set; } = 512;
    public int ChunkOverlapTokens { get; set; } = 64;
    public int ChunkMinTokens { get; set; } = 40;

    // Sprint 10: HyDE query rewriting via local llama.cpp sidecar.
    public HydeSettings Hyde { get; set; } = new();

    // Sprint 15: rule-based multi-query expansion for dense retrieval.
    public MultiQuerySettings MultiQuery { get; set; } = new();

    // Sprint 16: listwise re-rank of the cross-encoder's top-5 via a
    // local llama.cpp-served instruction-tuned model.
    public ListwiseRerankSettings ListwiseRerank { get; set; } = new();

    // Fails fast on nonsensical configuration before any model load or DB
    // connection. Without this a typo silently degrades behaviour — most
    // insidiously EmbeddingVariant, where an unrecognised value used to fall
    // straight through to the fp32 model path with no warning.
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("ConnectionString must not be empty.");
        }

        if (HttpPort is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"HttpPort must be in 1..65535; got {HttpPort}.");
        }

        if (!string.Equals(EmbeddingVariant, "fp32", StringComparison.Ordinal)
            && !string.Equals(EmbeddingVariant, "fp16", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"EmbeddingVariant must be 'fp32' or 'fp16'; got '{EmbeddingVariant}'.");
        }

        if (EmbeddingDim <= 0)
        {
            throw new InvalidOperationException($"EmbeddingDim must be positive; got {EmbeddingDim}.");
        }

        if (IngestBatchSize <= 0)
        {
            throw new InvalidOperationException($"IngestBatchSize must be positive; got {IngestBatchSize}.");
        }

        if (ChunkMinTokens <= 0 || ChunkTargetTokens <= 0 || ChunkMaxTokens <= 0)
        {
            throw new InvalidOperationException(
                "ChunkMinTokens, ChunkTargetTokens and ChunkMaxTokens must all be positive.");
        }

        if (ChunkOverlapTokens < 0)
        {
            throw new InvalidOperationException(
                $"ChunkOverlapTokens must not be negative; got {ChunkOverlapTokens}.");
        }

        if (!(ChunkMinTokens <= ChunkTargetTokens && ChunkTargetTokens <= ChunkMaxTokens))
        {
            throw new InvalidOperationException(
                "Chunk token bounds must satisfy ChunkMinTokens <= ChunkTargetTokens <= ChunkMaxTokens; got "
                + $"{ChunkMinTokens} / {ChunkTargetTokens} / {ChunkMaxTokens}.");
        }
    }
}
