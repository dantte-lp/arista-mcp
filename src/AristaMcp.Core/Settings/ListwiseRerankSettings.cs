namespace AristaMcp.Core.Settings;

// Sprint 16: list-wise re-rank of the cross-encoder's top-5 by a local
// llama.cpp-served instruction-tuned model. Off by default; opt-in via
// ARISTA_MCP__ListwiseRerank__Enabled=true. Targets the residual top-1
// gap that the cross-encoder leaves on ambiguous candidates.
public sealed class ListwiseRerankSettings
{
    public bool Enabled { get; set; }

    // OpenAI-compatible chat endpoint — same llama.cpp sidecar as HyDE.
    public string Endpoint { get; set; } = "http://127.0.0.1:8090/v1/chat/completions";

    public string Model { get; set; } = "qwen2.5-3b-instruct";

    // Listwise prompt is short (~600 input tokens for 5 candidates) but
    // generation is constrained to a comma-separated list of 5 letters,
    // ~10 tokens. 5 s is generous for that on Qwen2.5-3B.
    public int TimeoutMs { get; set; } = 5000;

    // Candidates fed to the LLM. Stick at 5 — more bloats prompt and
    // confuses the small instruction-tuned model. Plan v0.3 explicitly
    // gates the latency budget on top-5 only.
    public int MaxCandidates { get; set; } = 5;

    // Cache key is (query, ordered top-N chunk-id tuple). Same input
    // deterministically produces the same listwise output, so a hit on
    // bench reruns or repeated user queries is free.
    public int CacheCapacity { get; set; } = 256;

    // Same circuit-breaker shape as HyDE — a stuck sidecar would
    // otherwise pay the timeout per query.
    public int CircuitFailureThreshold { get; set; } = 5;

    public int CircuitCooldownSeconds { get; set; } = 60;
}
