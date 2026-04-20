using System.Diagnostics;
using AristaMcp.Core.Models;
using AristaMcp.Core.Retrieval;
using AristaMcp.Embedding;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace AristaMcp.Server.Retrieval;

// Hybrid retrieval:
//   1. Expand the query (Arista acronym annotations)
//   2. Embed the expanded query with the IEmbedder's query prefix
//   3. In parallel:
//        - dense: ORDER BY embedding <=> $1::halfvec (pgvector cosine)
//        - sparse: ORDER BY bm25v <&> to_bm25query(idx, tokenize(q, 'chunks_tokenizer')::bm25vector)
//   4. Reciprocal Rank Fusion with k=60 (RrfK)
//   5. Rerank top-N via IReranker
//   6. Emit diagnostics alongside results
public sealed class HybridRetriever(
    IEmbedder embedder,
    IReranker reranker,
    NpgsqlDataSource dataSource) : IHybridRetriever
{
    public async Task<SearchResponse> SearchAsync(
        string query,
        RetrievalOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        var total = Stopwatch.StartNew();
        var expansion = QueryExpander.Expand(query);

        var embedSw = Stopwatch.StartNew();
        var qVecs = await embedder.EmbedAsync([expansion.Expanded], isQuery: true, ct).ConfigureAwait(false);
        embedSw.Stop();
        var qVec = new HalfVector(qVecs[0].Select(f => (Half)f).ToArray());

        var denseTask = RunDenseAsync(qVec, options, ct);
        var sparseTask = RunSparseAsync(expansion.Expanded, options, ct);
        await Task.WhenAll(denseTask, sparseTask).ConfigureAwait(false);
        var denseRows = denseTask.Result;
        var sparseRows = sparseTask.Result;

        var rrfSw = Stopwatch.StartNew();
        var fused = ReciprocalRankFusion(denseRows, sparseRows, options.RrfK);
        rrfSw.Stop();

        var topForRerank = fused.Take(options.RerankTopN).ToList();
        var rerankSw = Stopwatch.StartNew();
        var rerankInput = topForRerank.Select(f => new RerankCandidate(f.Row.ChunkId, f.Row.Content)).ToList();
        var rerankResults = await reranker.RerankAsync(expansion.Expanded, rerankInput, ct).ConfigureAwait(false);
        rerankSw.Stop();

        var rerankScore = rerankResults.ToDictionary(r => r.ChunkId, r => r.Score);
        var ranked = topForRerank
            .OrderByDescending(f => rerankScore.TryGetValue(f.Row.ChunkId, out var s) ? s : 0f)
            .Take(options.Limit)
            .ToList();

        var results = ranked.Select(f => Build(f, rerankScore)).ToList();
        total.Stop();

        var diag = new SearchDiagnostics(
            DenseHits: denseRows.Count,
            SparseHits: sparseRows.Count,
            AfterRrf: fused.Count,
            AfterRerank: results.Count,
            EmbedMs: embedSw.Elapsed.TotalMilliseconds,
            DenseQueryMs: 0,
            SparseQueryMs: 0,
            RrfMs: rrfSw.Elapsed.TotalMilliseconds,
            RerankMs: rerankSw.Elapsed.TotalMilliseconds,
            TotalMs: total.Elapsed.TotalMilliseconds);

        return new SearchResponse(results, diag);
    }

    private async Task<List<CandidateRow>> RunDenseAsync(
        HalfVector qVec,
        RetrievalOptions options,
        CancellationToken ct)
    {
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

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter { Value = qVec });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Category });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Product });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = options.CandidatePoolSize });

        return await ReadRowsAsync(cmd, scoreSign: -1, ct).ConfigureAwait(false);
    }

    private async Task<List<CandidateRow>> RunSparseAsync(
        string query,
        RetrievalOptions options,
        CancellationToken ct)
    {
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

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = query });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Category });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Product });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = options.CandidatePoolSize });

        return await ReadRowsAsync(cmd, scoreSign: +1, ct).ConfigureAwait(false);
    }

    private static async Task<List<CandidateRow>> ReadRowsAsync(
        NpgsqlCommand cmd,
        int scoreSign,
        CancellationToken ct)
    {
        var rows = new List<CandidateRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var distance = reader.GetFloat(14);
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
                Distance: distance,
                // <=> returns [0..2] where 0 is perfect match; we want higher=better.
                // <&> returns negative BM25; we negate to get higher=better.
                RawScore: scoreSign * distance));
        }

        return rows;
    }

    private static List<FusedCandidate> ReciprocalRankFusion(
        List<CandidateRow> dense,
        List<CandidateRow> sparse,
        int k)
    {
        var scores = new Dictionary<long, (float Rrf, CandidateRow Row, int? DenseRank, int? SparseRank)>();

        for (var i = 0; i < dense.Count; i++)
        {
            var row = dense[i];
            var rrf = 1f / (k + i + 1);
            scores[row.ChunkId] = (rrf, row, i + 1, null);
        }

        for (var i = 0; i < sparse.Count; i++)
        {
            var row = sparse[i];
            var rrf = 1f / (k + i + 1);
            if (scores.TryGetValue(row.ChunkId, out var existing))
            {
                scores[row.ChunkId] = (existing.Rrf + rrf, existing.Row, existing.DenseRank, i + 1);
            }
            else
            {
                scores[row.ChunkId] = (rrf, row, null, i + 1);
            }
        }

        return [.. scores.Values
            .OrderByDescending(x => x.Rrf)
            .Select(x => new FusedCandidate(x.Row, x.Rrf, x.DenseRank, x.SparseRank))];
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
            DenseSimilarity: f.DenseRank is null ? null : f.Row.RawScore,
            Bm25Score: f.SparseRank is null ? null : -f.Row.Distance,
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
        float Distance,
        float RawScore);

    private sealed record FusedCandidate(
        CandidateRow Row,
        float RrfScore,
        int? DenseRank,
        int? SparseRank);
}
