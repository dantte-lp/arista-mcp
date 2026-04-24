namespace AristaMcp.Core.Retrieval;

// Output of IHydeExpander.ExpandAsync. DenseQuery is the string the
// retriever should embed; the rest is instrumentation so the caller can
// populate SearchDiagnostics without re-measuring inside the retriever.
public readonly record struct HydeResult(
    string DenseQuery,
    double LatencyMs,
    bool CacheHit,
    bool UsedFallback);
