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
    double TotalMs);
