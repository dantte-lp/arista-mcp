namespace AristaMcp.Core.Settings;

// HyDE rewrites the user query into a hypothetical answer paragraph via
// a local LLM, then embeds that paragraph for dense retrieval. BM25 and
// the reranker stay on the raw query. Off by default; set the Enabled
// flag or env var ARISTA_MCP__Hyde__Enabled=true to opt in.
public sealed class HydeSettings
{
    public bool Enabled { get; set; }

    // OpenAI-compatible chat endpoint — llama.cpp sidecar default.
    public string Endpoint { get; set; } = "http://127.0.0.1:8090/v1/chat/completions";

    // Reported to the server in the `model` field; llama.cpp accepts any
    // string here (it serves whatever -m model was loaded), but a clear
    // name helps when routing through a proxy later.
    public string Model { get; set; } = "qwen2.5-1.5b-instruct";

    // Total per-request timeout. Observed Qwen2.5-1.5B Q4_K_M on a 12-core
    // CPU runs the full rewrite in 2.7-3.2 s cold; 6 s leaves headroom for
    // the occasional slow generation while still falling back cleanly when
    // the sidecar is genuinely stuck.
    public int TimeoutMs { get; set; } = 6000;

    // Output budget for the hypothetical answer. 120 tokens ~= 80 words —
    // enough to carry Arista-specific phrasing into the embedding without
    // paying for tail-end drift where the model starts hallucinating.
    public int MaxTokens { get; set; } = 120;

    // Keep capacity small — repeat-query flows converge fast, memory is cheap.
    public int CacheCapacity { get; set; } = 512;

    // Circuit breaker: if the sidecar returns this many consecutive failures,
    // skip HyDE for CooldownSeconds before retrying. Keeps the fallback path
    // from hammering a dead endpoint on every query.
    public int CircuitFailureThreshold { get; set; } = 5;

    public int CircuitCooldownSeconds { get; set; } = 60;
}
