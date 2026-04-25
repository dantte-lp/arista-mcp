namespace AristaMcp.Core.Settings;

// Sprint 15: multi-query dense retrieval. Off by default until the
// v0.2.5 bench rows confirm the rule-based variants don't regress
// top-K recall on niche queries. Enable via env var
// ARISTA_MCP__MultiQuery__Enabled=true.
public sealed class MultiQuerySettings
{
    public bool Enabled { get; set; }
}
