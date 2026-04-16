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
}
