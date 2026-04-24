using System.Diagnostics;
using System.Runtime.InteropServices;
using AristaMcp.Core.Models;
using AristaMcp.Core.Observability;
using AristaMcp.Core.Retrieval;
using AristaMcp.Embedding;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace AristaMcp.Server.Retrieval;

// Hybrid retrieval:
//   1. Expand the query (Arista acronym annotations)
//   2. HyDE: rewrite into a hypothetical answer paragraph (when enabled).
//      Only the DENSE path uses the rewritten text — BM25 and the reranker
//      both keep the raw expanded query (HyDE hallucinations would poison
//      lexical matching and confuse a cross-encoder).
//   3. Embed the dense query with the IEmbedder's query prefix
//   4. In parallel:
//        - dense: ORDER BY embedding <=> $1::halfvec (pgvector cosine)
//        - sparse: ORDER BY bm25v <&> to_bm25query(idx, tokenize(q, 'chunks_tokenizer')::bm25vector)
//   5. Reciprocal Rank Fusion with k=60 (RrfK) — tracks BOTH distances per co-hit so
//      diagnostics report accurate DenseSimilarity + Bm25Score even for fused chunks.
//   6. Rerank top-N via IReranker
//   7. Emit diagnostics alongside results
public sealed class HybridRetriever : IHybridRetriever
{
    private readonly IEmbedder _embedder;
    private readonly IReranker _reranker;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IHydeExpander _hyde;

    public HybridRetriever(
        IEmbedder embedder,
        IReranker reranker,
        NpgsqlDataSource dataSource,
        IHydeExpander? hyde = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(dataSource);
        _embedder = embedder;
        _reranker = reranker;
        _dataSource = dataSource;
        _hyde = hyde ?? new NoopHydeExpander();
    }

    // Size is deliberately small — retrieval workloads have tight hot-sets
    // (top ~100 Claude-issued queries per session). Larger just wastes RAM.
    private readonly QueryEmbeddingCache _queryCache = new(capacity: 256);

    // Below this RRF-score spread between top-1 and top-5, the rerank signal
    // is noise — we cap rerank work to AdaptiveRerankFloor to save ~50 ms/query.
    // Tuned empirically on v0.1.3 bench history; revisit if rerank model changes.
    private const float AdaptiveSpreadThreshold = 0.02f;
    private const int AdaptiveRerankFloor = 10;

    public async Task<SearchResponse> SearchAsync(
        string query,
        RetrievalOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        using var outerSpan = AristaActivity.Source.StartActivity(AristaActivity.Operations.SearchHybrid);
        outerSpan?.SetTag(AristaActivity.Tags.QueryLength, query.Length);
        if (options.Category is not null) outerSpan?.SetTag(AristaActivity.Tags.Category, options.Category);
        if (options.Product is not null) outerSpan?.SetTag(AristaActivity.Tags.Product, options.Product);

        var total = Stopwatch.StartNew();
        var expansion = QueryExpander.Expand(query);

        // HyDE rewrite — only feeds the dense path. On timeout / 5xx / disabled
        // the expander returns the raw expansion so retrieval never blocks.
        var hyde = await _hyde.ExpandAsync(expansion.Expanded, ct).ConfigureAwait(false);
        var denseQuery = hyde.DenseQuery;

        var embedSw = Stopwatch.StartNew();
        HalfVector qVec;
        bool cacheHit;
        if (_queryCache.TryGet(denseQuery, out var cached))
        {
            qVec = cached;
            cacheHit = true;
        }
        else
        {
            using var embedSpan = AristaActivity.Source.StartActivity(AristaActivity.Operations.SearchEmbed);
            var qVecs = await _embedder.EmbedAsync(
                [denseQuery], isQuery: true, ct).ConfigureAwait(false);
            Half[] halfArr = [.. qVecs[0].Select(static f => (Half)f)];
            qVec = new HalfVector(halfArr);
            _queryCache.Add(denseQuery, qVec);
            cacheHit = false;
        }
        embedSw.Stop();
        outerSpan?.SetTag(AristaActivity.Tags.CacheHit, cacheHit);

        var denseTask = RunDenseAsync(qVec, options, ct);
        var sparseTask = RunSparseAsync(expansion.Expanded, options, ct);
        await Task.WhenAll(denseTask, sparseTask).ConfigureAwait(false);
        var (denseRows, denseMs) = denseTask.Result;
        var (sparseRows, sparseMs) = sparseTask.Result;

        var rrfSw = Stopwatch.StartNew();
        var fused = ReciprocalRankFusion(denseRows, sparseRows, options.RrfK);
        rrfSw.Stop();

        // Adaptive rerank: tight-cluster top-5 = rerank signal is noise, cap to floor.
        var rerankTopN = ComputeAdaptiveRerankTopN(fused, options.RerankTopN);
        var topForRerank = fused.Take(rerankTopN).ToList();
        outerSpan?.SetTag(AristaActivity.Tags.RerankTopN, rerankTopN);
        outerSpan?.SetTag(AristaActivity.Tags.RerankAdaptive, rerankTopN < options.RerankTopN);

        var rerankSw = Stopwatch.StartNew();
        IReadOnlyList<RerankResult> rerankResults;
        // Span closes when the scope block exits; do NOT also call Dispose()
        // explicitly — Activity.Dispose internally re-Stop()s and corrupts the
        // span end timestamp. (Sprint 8 audit finding.)
        using (AristaActivity.Source.StartActivity(AristaActivity.Operations.SearchRerank))
        {
            var rerankInput = topForRerank.Select(f => new RerankCandidate(f.Row.ChunkId, f.Row.Content)).ToList();
            rerankResults = await _reranker.RerankAsync(expansion.Expanded, rerankInput, ct).ConfigureAwait(false);
        }
        rerankSw.Stop();

        var rerankScore = rerankResults.ToDictionary(r => r.ChunkId, r => r.Score);
        var reranked = topForRerank
            .OrderByDescending(f => rerankScore.TryGetValue(f.Row.ChunkId, out var s) ? s : 0f);

        var ranked = options.DedupPerSection
            ? DedupPerSection(reranked).Take(options.Limit).ToList()
            : reranked.Take(options.Limit).ToList();

        var results = ranked.Select(f => Build(f, rerankScore)).ToList();
        total.Stop();

        var diag = new SearchDiagnostics(
            DenseHits: denseRows.Count,
            SparseHits: sparseRows.Count,
            AfterRrf: fused.Count,
            AfterRerank: results.Count,
            EmbedMs: embedSw.Elapsed.TotalMilliseconds,
            DenseQueryMs: denseMs,
            SparseQueryMs: sparseMs,
            RrfMs: rrfSw.Elapsed.TotalMilliseconds,
            RerankMs: rerankSw.Elapsed.TotalMilliseconds,
            TotalMs: total.Elapsed.TotalMilliseconds,
            HydeMs: hyde.LatencyMs,
            HydeHit: hyde.CacheHit,
            HydeFallback: hyde.UsedFallback);

        outerSpan?.SetTag(AristaActivity.Tags.DenseHits, denseRows.Count);
        outerSpan?.SetTag(AristaActivity.Tags.SparseHits, sparseRows.Count);

        return new SearchResponse(results, diag);
    }

    private async Task<(List<CandidateRow> Rows, double ElapsedMs)> RunDenseAsync(
        HalfVector qVec,
        RetrievalOptions options,
        CancellationToken ct)
    {
        using var span = AristaActivity.Source.StartActivity(AristaActivity.Operations.SearchDense);
        const string sql = """
            SELECT c.id, c.document_id, c.chunk_index, c.content, c.raw_content,
                   c.section_title, c.section_level, c.page_start, c.page_end,
                   d.title, d.slug, d.category, d.product, d.version,
                   c.embedding <=> $1 AS distance
            FROM chunks c
            JOIN documents d ON d.id = c.document_id
            WHERE ($2::text IS NULL OR d.category = $2)
              AND ($3::text IS NULL OR d.product = $3)
            ORDER BY c.embedding <=> $1
            LIMIT $4;
            """;

        var sw = Stopwatch.StartNew();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter { Value = qVec });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Category });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Product });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = options.CandidatePoolSize });

        var rows = await ReadRowsAsync(cmd, ct).ConfigureAwait(false);
        sw.Stop();
        return (rows, sw.Elapsed.TotalMilliseconds);
    }

    private async Task<(List<CandidateRow> Rows, double ElapsedMs)> RunSparseAsync(
        string query,
        RetrievalOptions options,
        CancellationToken ct)
    {
        using var span = AristaActivity.Source.StartActivity(AristaActivity.Operations.SearchSparse);
        const string sql = """
            SELECT c.id, c.document_id, c.chunk_index, c.content, c.raw_content,
                   c.section_title, c.section_level, c.page_start, c.page_end,
                   d.title, d.slug, d.category, d.product, d.version,
                   c.bm25v <&> to_bm25query(
                       'idx_chunks_bm25'::regclass,
                       tokenizer_catalog.tokenize($1, 'chunks_tokenizer')::bm25vector) AS distance
            FROM chunks c
            JOIN documents d ON d.id = c.document_id
            WHERE ($2::text IS NULL OR d.category = $2)
              AND ($3::text IS NULL OR d.product = $3)
            ORDER BY c.bm25v <&> to_bm25query(
                'idx_chunks_bm25'::regclass,
                tokenizer_catalog.tokenize($1, 'chunks_tokenizer')::bm25vector)
            LIMIT $4;
            """;

        var sw = Stopwatch.StartNew();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = query });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Category });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Product });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = options.CandidatePoolSize });

        var rows = await ReadRowsAsync(cmd, ct).ConfigureAwait(false);
        sw.Stop();
        return (rows, sw.Elapsed.TotalMilliseconds);
    }

    private static async Task<List<CandidateRow>> ReadRowsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<CandidateRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new CandidateRow(
                ChunkId: reader.GetInt64(0),
                DocumentId: reader.GetString(1),
                ChunkIndex: reader.GetInt32(2),
                Content: reader.GetString(3),
                RawContent: reader.GetString(4),
                SectionTitle: reader.IsDBNull(5) ? null : reader.GetString(5),
                SectionLevel: reader.IsDBNull(6) ? null : reader.GetInt16(6),
                PageStart: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                PageEnd: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                DocumentTitle: reader.GetString(9),
                DocumentSlug: reader.GetString(10),
                Category: reader.GetString(11),
                Product: reader.IsDBNull(12) ? null : reader.GetString(12),
                Version: reader.IsDBNull(13) ? null : reader.GetString(13),
                Distance: reader.GetFloat(14)));
        }

        return rows;
    }

    // If the top-5 RRF scores are within AdaptiveSpreadThreshold, candidates
    // are effectively tied and sending 30 of them to the cross-encoder is
    // wasted compute — the reranker will assign near-equal scores and the
    // RRF order survives. Cap to AdaptiveRerankFloor (10) in that case.
    private static int ComputeAdaptiveRerankTopN(List<FusedCandidate> fused, int configured)
    {
        if (fused.Count < 5 || configured <= AdaptiveRerankFloor)
        {
            return configured;
        }
        var top1 = fused[0].RrfScore;
        var top5 = fused[4].RrfScore;
        var spread = top1 - top5;
        return spread > AdaptiveSpreadThreshold ? configured : AdaptiveRerankFloor;
    }

    private static List<FusedCandidate> ReciprocalRankFusion(
        List<CandidateRow> dense,
        List<CandidateRow> sparse,
        int k)
    {
        var scores = new Dictionary<long, Accumulator>(dense.Count + sparse.Count);

        for (var i = 0; i < dense.Count; i++)
        {
            var row = dense[i];
            var rrf = 1f / (k + i + 1);
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(scores, row.ChunkId, out var existed);
            slot = existed
                ? slot with { Rrf = slot.Rrf + rrf, DenseRank = i + 1, DenseDistance = row.Distance }
                : new Accumulator(rrf, row, i + 1, null, row.Distance, null);
        }

        for (var i = 0; i < sparse.Count; i++)
        {
            var row = sparse[i];
            var rrf = 1f / (k + i + 1);
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(scores, row.ChunkId, out var existed);
            slot = existed
                ? slot with { Rrf = slot.Rrf + rrf, SparseRank = i + 1, SparseDistance = row.Distance }
                : new Accumulator(rrf, row, null, i + 1, null, row.Distance);
        }

        return [.. scores.Values
            .OrderByDescending(x => x.Rrf)
            .Select(x => new FusedCandidate(x.Row, x.Rrf, x.DenseRank, x.SparseRank, x.DenseDistance, x.SparseDistance))];
    }

    // Emits at most one chunk per (document_id, section_title ?? raw_content-prefix) —
    // whichever scored highest post-rerank. Leaves the order of the retained chunks
    // exactly as the caller passed in. Chunks with null section_title are still deduped
    // by document_id only (a single null section chunk per doc).
    private static IEnumerable<FusedCandidate> DedupPerSection(IEnumerable<FusedCandidate> ranked)
    {
        var seen = new HashSet<(string DocumentId, string SectionKey)>();
        foreach (var f in ranked)
        {
            var key = (f.Row.DocumentId, f.Row.SectionTitle ?? string.Empty);
            if (seen.Add(key))
            {
                yield return f;
            }
        }
    }

    private static ChunkResult Build(FusedCandidate f, Dictionary<long, float> rerankScore) =>
        new(
            ChunkId: f.Row.ChunkId,
            DocumentId: f.Row.DocumentId,
            DocumentTitle: f.Row.DocumentTitle,
            DocumentSlug: f.Row.DocumentSlug,
            Category: f.Row.Category,
            Product: f.Row.Product,
            Version: f.Row.Version,
            SectionTitle: f.Row.SectionTitle,
            SectionLevel: f.Row.SectionLevel,
            PageStart: f.Row.PageStart,
            PageEnd: f.Row.PageEnd,
            RawContent: f.Row.RawContent,
            Score: rerankScore.TryGetValue(f.Row.ChunkId, out var rs) ? rs : f.RrfScore,
            // pgvector's <=> cosine distance ∈ [0, 2]; similarity = 1 - distance ∈ [-1, 1].
            DenseSimilarity: f.DenseDistance is null ? null : 1f - f.DenseDistance.Value,
            // vchord_bm25's <&> returns the negative BM25 score; flip sign so callers
            // see a conventional "higher = more relevant" value.
            Bm25Score: f.SparseDistance is null ? null : -f.SparseDistance.Value,
            RrfScore: f.RrfScore,
            RerankScore: rerankScore.TryGetValue(f.Row.ChunkId, out var rs2) ? rs2 : null);

    private sealed record CandidateRow(
        long ChunkId,
        string DocumentId,
        int ChunkIndex,
        string Content,
        string RawContent,
        string? SectionTitle,
        short? SectionLevel,
        int? PageStart,
        int? PageEnd,
        string DocumentTitle,
        string DocumentSlug,
        string Category,
        string? Product,
        string? Version,
        float Distance);

    private sealed record FusedCandidate(
        CandidateRow Row,
        float RrfScore,
        int? DenseRank,
        int? SparseRank,
        float? DenseDistance,
        float? SparseDistance);

    private record struct Accumulator(
        float Rrf,
        CandidateRow Row,
        int? DenseRank,
        int? SparseRank,
        float? DenseDistance,
        float? SparseDistance);
}
