namespace AristaMcp.Core.Models;

public sealed record SearchDiagnostics(
    int DenseHits,
    int SparseHits,
    int AfterRrf,
    int AfterRerank,
    double EmbedMs,
    double DenseQueryMs,
    double SparseQueryMs,
    double RrfMs,
    double RerankMs,
    double TotalMs,
    // Sprint 10: HyDE timing. HydeMs=0 and HydeHit=false on a NoopHydeExpander
    // or when disabled, so default-args keep pre-HyDE callers compiling.
    double HydeMs = 0,
    bool HydeHit = false,
    bool HydeFallback = false);
