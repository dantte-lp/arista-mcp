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
    private readonly IMultiQueryExpander _multiQuery;
    private readonly IListwiseReranker _listwise;

    public HybridRetriever(
        IEmbedder embedder,
        IReranker reranker,
        NpgsqlDataSource dataSource,
        IHydeExpander? hyde = null,
        IMultiQueryExpander? multiQuery = null,
        IListwiseReranker? listwise = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(dataSource);
        _embedder = embedder;
        _reranker = reranker;
        _dataSource = dataSource;
        _hyde = hyde ?? new NoopHydeExpander();
        _multiQuery = multiQuery ?? new NoopMultiQueryExpander();
        _listwise = listwise ?? new NoopListwiseReranker();
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
        var primaryDenseQuery = hyde.DenseQuery;

        // Multi-query expansion — produces 1..N rule-based variants.
        // Always includes primaryDenseQuery as the first entry. BM25 and
        // the reranker stay on expansion.Expanded; only dense widens.
        var denseVariants = _multiQuery.Expand(primaryDenseQuery);

        var embedSw = Stopwatch.StartNew();
        var qVecs = new List<HalfVector>(denseVariants.Count);
        var anyCacheHit = false;
        var anyCacheMiss = false;
        foreach (var variant in denseVariants)
        {
            if (_queryCache.TryGet(variant, out var cached))
            {
                qVecs.Add(cached);
                anyCacheHit = true;
                continue;
            }
            using var embedSpan = AristaActivity.Source.StartActivity(AristaActivity.Operations.SearchEmbed);
            var emb = await _embedder.EmbedAsync(
                [variant], isQuery: true, ct).ConfigureAwait(false);
            Half[] halfArr = [.. emb[0].Select(static f => (Half)f)];
            var qv = new HalfVector(halfArr);
            _queryCache.Add(variant, qv);
            qVecs.Add(qv);
            anyCacheMiss = true;
        }
        embedSw.Stop();
        // Cache hit reported as "all variants hit" — useful signal for warm vs cold queries.
        outerSpan?.SetTag(AristaActivity.Tags.CacheHit, anyCacheHit && !anyCacheMiss);

        // Dense: N parallel SQL scans, then union by chunk_id keeping best rank.
        var denseTasks = qVecs
            .Select(qv => RunDenseAsync(qv, options, ct))
            .ToArray();
        var sparseTask = RunSparseAsync(expansion.Expanded, options, ct);
        await Task.WhenAll([..denseTasks, sparseTask]).ConfigureAwait(false);

        var denseResults = denseTasks.Select(t => t.Result).ToArray();
        var denseRows = denseResults.Length == 1
            ? denseResults[0].Rows
            : UnionByBestRank(denseResults.Select(r => r.Rows));
        // Report MAX dense latency since variants ran in parallel — not the sum.
        var denseMs = denseResults.Max(r => r.ElapsedMs);
        var (sparseRows, sparseMs) = sparseTask.Result;

        var rrfSw = Stopwatch.StartNew();
        var fused = ReciprocalRankFusion(denseRows, sparseRows, options.RrfK);
        rrfSw.Stop();

        // Adaptive rerank: tight-cluster top-5 = rerank signal is noise, cap to floor.
        var rerankTopN = ComputeAdaptiveRerankTopN(fused, options.RerankTopN);
        var topForRerank = fused.Take(rerankTopN).ToList();
        outerSpan?.SetTag(AristaActivity.Tags.RerankTopN, rerankTopN);
        outerSpan?.SetTag(AristaActivity.Tags.RerankAdaptive, rerankTopN < options.RerankTopN);

        // Sprint 15: parent hydration. For leaves with a parent_chunk_id we
        // pull the parent's raw_content in a single batch SELECT so the
        // cross-encoder reranker scores against the richer section context.
        // Leaves without a parent (legacy rows from before v0.2.5 reingest)
        // fall back to leaf content — same shape, same wire format.
        var distinctParentIds = topForRerank
            .Where(f => f.Row.ParentChunkId.HasValue)
            .Select(f => f.Row.ParentChunkId!.Value)
            .Distinct()
            .ToArray();
        var parentTexts = await FetchParentTextsAsync(distinctParentIds, ct).ConfigureAwait(false);

        var rerankSw = Stopwatch.StartNew();
        IReadOnlyList<RerankResult> rerankResults;
        // Span closes when the scope block exits; do NOT also call Dispose()
        // explicitly — Activity.Dispose internally re-Stop()s and corrupts the
        // span end timestamp. (Sprint 8 audit finding.)
        using (AristaActivity.Source.StartActivity(AristaActivity.Operations.SearchRerank))
        {
            var rerankInput = topForRerank.Select(f =>
            {
                var rerankText = f.Row.ParentChunkId is long pid
                    && parentTexts.TryGetValue(pid, out var pt)
                        ? pt
                        : f.Row.RawContent;
                return new RerankCandidate(f.Row.ChunkId, rerankText);
            }).ToList();
            rerankResults = await _reranker.RerankAsync(expansion.Expanded, rerankInput, ct).ConfigureAwait(false);
        }
        rerankSw.Stop();

        var rerankScore = rerankResults.ToDictionary(r => r.ChunkId, r => r.Score);
        var rerankedList = topForRerank
            .OrderByDescending(f => rerankScore.TryGetValue(f.Row.ChunkId, out var s) ? s : 0f)
            .ToList();

        // Sprint 16: listwise re-rank of the cross-encoder's top-N. Skip
        // when the implementation reports MaxCandidates=0 (Noop or
        // disabled). Listwise sees parent text just like the cross-encoder
        // — the same content the LLM would have most signal on.
        IReadOnlyList<FusedCandidate> reranked = rerankedList;
        var listwiseLatencyMs = 0d;
        var listwiseHit = false;
        var listwiseFallback = false;
        if (_listwise.MaxCandidates > 0 && rerankedList.Count > 1)
        {
            var sliceCount = Math.Min(_listwise.MaxCandidates, rerankedList.Count);
            var listwiseInput = new List<RerankCandidate>(sliceCount);
            for (var i = 0; i < sliceCount; i++)
            {
                var row = rerankedList[i].Row;
                var text = row.ParentChunkId is long pid
                    && parentTexts.TryGetValue(pid, out var pt)
                        ? pt
                        : row.RawContent;
                listwiseInput.Add(new RerankCandidate(row.ChunkId, text));
            }

            using (AristaActivity.Source.StartActivity(AristaActivity.Operations.SearchRerank))
            {
                var listwiseResult = await _listwise.ReorderAsync(
                    expansion.Expanded, listwiseInput, ct).ConfigureAwait(false);
                listwiseLatencyMs = listwiseResult.LatencyMs;
                listwiseHit = listwiseResult.CacheHit;
                listwiseFallback = listwiseResult.UsedFallback;

                if (!listwiseFallback)
                {
                    // Reorder the prefix by the LLM's permutation; the tail
                    // beyond MaxCandidates keeps cross-encoder order.
                    var byId = new Dictionary<long, FusedCandidate>(sliceCount);
                    for (var i = 0; i < sliceCount; i++)
                    {
                        byId[rerankedList[i].Row.ChunkId] = rerankedList[i];
                    }
                    var newPrefix = new List<FusedCandidate>(sliceCount);
                    foreach (var id in listwiseResult.OrderedChunkIds)
                    {
                        if (byId.Remove(id, out var f))
                        {
                            newPrefix.Add(f);
                        }
                    }
                    // Defensive: any candidate the LLM dropped goes to the
                    // end of the prefix in cross-encoder order.
                    if (byId.Count > 0)
                    {
                        for (var i = 0; i < sliceCount; i++)
                        {
                            if (byId.ContainsKey(rerankedList[i].Row.ChunkId))
                            {
                                newPrefix.Add(rerankedList[i]);
                            }
                        }
                    }

                    var stitched = new List<FusedCandidate>(rerankedList.Count);
                    stitched.AddRange(newPrefix);
                    for (var i = sliceCount; i < rerankedList.Count; i++)
                    {
                        stitched.Add(rerankedList[i]);
                    }
                    reranked = stitched;
                }
            }
        }

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
            HydeFallback: hyde.UsedFallback,
            ListwiseMs: listwiseLatencyMs,
            ListwiseHit: listwiseHit,
            ListwiseFallback: listwiseFallback);

        outerSpan?.SetTag(AristaActivity.Tags.DenseHits, denseRows.Count);
        outerSpan?.SetTag(AristaActivity.Tags.SparseHits, sparseRows.Count);

        return new SearchResponse(results, diag);
    }

    // Union N dense result lists into a single list, keeping each chunk
    // once at its best (lowest) rank across variants. The result is
    // ordered by that best rank — what RRF then folds with sparse.
    // For 1 input list the function is a no-op (callers short-circuit).
    private static List<CandidateRow> UnionByBestRank(IEnumerable<List<CandidateRow>> rowLists)
    {
        var bestByChunk = new Dictionary<long, (int Rank, CandidateRow Row)>();
        foreach (var list in rowLists)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var row = list[rank];
                if (!bestByChunk.TryGetValue(row.ChunkId, out var existing)
                    || rank < existing.Rank)
                {
                    bestByChunk[row.ChunkId] = (rank, row);
                }
            }
        }
        return [.. bestByChunk.Values
            .OrderBy(static x => x.Rank)
            .Select(static x => x.Row)];
    }

    private async Task<(List<CandidateRow> Rows, double ElapsedMs)> RunDenseAsync(
        HalfVector qVec,
        RetrievalOptions options,
        CancellationToken ct)
    {
        using var span = AristaActivity.Source.StartActivity(AristaActivity.Operations.SearchDense);
        // Filter chunk_kind='leaf' so the dense path never returns parent
        // rows (their embedding is NULL anyway, but the predicate also
        // means the planner can stop after the first leaf-only HNSW probe
        // on a partial index when one is added later).
        const string sql = """
            SELECT c.id, c.document_id, c.chunk_index, c.content, c.raw_content,
                   c.section_title, c.section_level, c.page_start, c.page_end,
                   c.parent_chunk_id,
                   d.title, d.slug, d.category, d.product, d.version,
                   c.embedding <=> $1 AS distance
            FROM chunks c
            JOIN documents d ON d.id = c.document_id
            WHERE c.chunk_kind = 'leaf'
              AND ($2::text IS NULL OR d.category = $2)
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
        // chunk_kind='leaf' filter mirrors the dense path. Without it, parent
        // rows (which the BM25 trigger ALSO indexes since bm25v is populated
        // for every row including parents) would surface in sparse results.
        const string sql = """
            SELECT c.id, c.document_id, c.chunk_index, c.content, c.raw_content,
                   c.section_title, c.section_level, c.page_start, c.page_end,
                   c.parent_chunk_id,
                   d.title, d.slug, d.category, d.product, d.version,
                   c.bm25v <&> to_bm25query(
                       'idx_chunks_bm25'::regclass,
                       tokenizer_catalog.tokenize($1, 'chunks_tokenizer')::bm25vector) AS distance
            FROM chunks c
            JOIN documents d ON d.id = c.document_id
            WHERE c.chunk_kind = 'leaf'
              AND ($2::text IS NULL OR d.category = $2)
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
                ParentChunkId: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                DocumentTitle: reader.GetString(10),
                DocumentSlug: reader.GetString(11),
                Category: reader.GetString(12),
                Product: reader.IsDBNull(13) ? null : reader.GetString(13),
                Version: reader.IsDBNull(14) ? null : reader.GetString(14),
                Distance: reader.GetFloat(15)));
        }

        return rows;
    }

    // Sprint 15: parent hydration. Pulls raw_content for the given parent
    // chunk ids in a single batch select so the cross-encoder reranker can
    // score (query, parent_text) instead of (query, leaf_text). One DB
    // round-trip per search; tiny — typically 10-20 unique parents per
    // query after rerank cap.
    private async Task<Dictionary<long, string>> FetchParentTextsAsync(
        long[] parentIds, CancellationToken ct)
    {
        var map = new Dictionary<long, string>(parentIds.Length);
        if (parentIds.Length == 0)
        {
            return map;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, raw_content FROM chunks WHERE id = ANY($1)";
        cmd.Parameters.Add(new NpgsqlParameter<long[]> { TypedValue = parentIds });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            map[reader.GetInt64(0)] = reader.GetString(1);
        }
        return map;
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
        long? ParentChunkId,
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
