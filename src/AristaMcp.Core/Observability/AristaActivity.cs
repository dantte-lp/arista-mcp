using System.Diagnostics;

namespace AristaMcp.Core.Observability;

// Activity source hooks for arista-mcp. Using System.Diagnostics.ActivitySource
// directly (no OpenTelemetry package dependency) means consumers can wire an
// OTLP exporter externally via OpenTelemetry.Extensions.Hosting without
// AristaMcp.* projects taking on the dep.
//
// The exporter registration (OTLP / Jaeger / console) lives in the host
// process (see docs/otel.md for the recipe); source name is the stable
// contract — don't rename.
public static class AristaActivity
{
    public const string SourceName = "AristaMcp";

    // Version bumps only when breaking the tag shape (rare). Minor schema
    // additions — new tag keys — are backward-compatible and don't require
    // a version bump.
    public const string Version = "1.0.0";

    public static readonly ActivitySource Source = new(SourceName, Version);

    // Named operations. Using constants rather than magic strings so a
    // grep for callsites is sufficient to find all spans of a given kind.
    public static class Operations
    {
        public const string SearchHybrid = "search.hybrid";
        public const string SearchDense = "search.dense";
        public const string SearchSparse = "search.sparse";
        public const string SearchRerank = "search.rerank";
        public const string SearchEmbed = "search.embed";
        public const string IngestDocument = "ingest.document";
        public const string IngestSubBatch = "ingest.subbatch";
    }

    // Tag key conventions — OTel attribute naming, dotted, lowercase.
    public static class Tags
    {
        public const string QueryLength = "arista.query.length";
        public const string CacheHit = "arista.cache.hit";
        public const string Category = "arista.category";
        public const string Product = "arista.product";
        public const string DenseHits = "arista.dense.hits";
        public const string SparseHits = "arista.sparse.hits";
        public const string RerankTopN = "arista.rerank.topn";
        public const string RerankAdaptive = "arista.rerank.adaptive";
        public const string DocId = "arista.doc.id";
        public const string DocSlug = "arista.doc.slug";
        public const string ChunkCount = "arista.chunk.count";
        public const string SubBatchIndex = "arista.subbatch.index";
        public const string SubBatchTotal = "arista.subbatch.total";
    }
}
