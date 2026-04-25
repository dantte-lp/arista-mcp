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
    public int IngestParallelism { get; set; } = 4;
    public int ChunkMaxTokens { get; set; } = 1200;
    public int ChunkTargetTokens { get; set; } = 512;
    public int ChunkOverlapTokens { get; set; } = 64;
    public int ChunkMinTokens { get; set; } = 40;

    // Sprint 10: HyDE query rewriting via local llama.cpp sidecar.
    public HydeSettings Hyde { get; set; } = new();

    // Sprint 15: rule-based multi-query expansion for dense retrieval.
    public MultiQuerySettings MultiQuery { get; set; } = new();
}
