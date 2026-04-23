using System.Text.Json;
using AristaMcp.Cli.Benchmarks;
using AristaMcp.Core.Models;
using AristaMcp.Server.Retrieval;

namespace AristaMcp.Cli.Curation;

// Collects (query, positive, negatives) triples from bench-query retrieval output
// for cross-encoder reranker fine-tuning. IHybridRetriever is injected so tests
// can stub it; production wires the real HybridRetriever.
public static class TripleCurator
{
    // RRF window we search in for candidates. The cross-encoder needs hard
    // negatives (plausibly-wrong docs), not random ones — pulling from the
    // top 20 retrieval results gives us the hard-negative pool automatically.
    public const int DefaultCandidatePool = 20;

    public static async Task<(IReadOnlyList<TripleCandidate> Triples, TripleCurationStats Stats)> CurateAsync(
        IReadOnlyList<BenchmarkQuery> queries,
        IHybridRetriever retriever,
        int negativesPerQuery,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(retriever);

        if (negativesPerQuery < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(negativesPerQuery),
                negativesPerQuery,
                "at least one hard negative required per query");
        }

        var output = new List<TripleCandidate>(queries.Count);
        var withPositive = 0;
        var skippedNoPositive = 0;
        var skippedInsufficientNegatives = 0;

        foreach (var q in queries)
        {
            ct.ThrowIfCancellationRequested();

            var resp = await retriever.SearchAsync(
                q.Query,
                new RetrievalOptions
                {
                    Limit = DefaultCandidatePool,
                    CandidatePoolSize = DefaultCandidatePool * 2,
                    RerankTopN = DefaultCandidatePool,
                    DedupPerSection = true,
                },
                ct).ConfigureAwait(false);

            var positive = PickPositive(resp.Results, q);
            if (positive is null)
            {
                skippedNoPositive++;
                continue;
            }

            withPositive++;
            var negatives = PickHardNegatives(resp.Results, positive, negativesPerQuery);
            if (negatives.Count < negativesPerQuery)
            {
                skippedInsufficientNegatives++;
                continue;
            }

            output.Add(new TripleCandidate(
                Query: q.Query,
                Positive: ToEntry(positive),
                Negatives: [.. negatives.Select(ToEntry)]));
        }

        return (output, new TripleCurationStats(
            QueriesTotal: queries.Count,
            QueriesWithPositive: withPositive,
            TriplesEmitted: output.Count,
            QueriesSkippedNoPositive: skippedNoPositive,
            QueriesSkippedInsufficientNegatives: skippedInsufficientNegatives));
    }

    private static ChunkResult? PickPositive(IReadOnlyList<ChunkResult> results, BenchmarkQuery q) =>
        results.FirstOrDefault(r => Matches(r, q));

    private static List<ChunkResult> PickHardNegatives(
        IReadOnlyList<ChunkResult> results,
        ChunkResult positive,
        int count)
    {
        // Skip the positive itself, sibling chunks (same doc), and same-product
        // chunks — the cross-encoder needs negatives from a DIFFERENT product
        // space to learn a useful ranking boundary. Same-product ranks are
        // ambiguous and make margin loss converge to no-op.
        var candidates = results.Where(r =>
            r.ChunkId != positive.ChunkId
            && !string.Equals(r.DocumentId, positive.DocumentId, StringComparison.Ordinal)
            && !ProductMatches(r.Product, positive.Product));

        var negatives = new List<ChunkResult>(count);
        foreach (var r in candidates)
        {
            negatives.Add(r);
            if (negatives.Count >= count)
            {
                break;
            }
        }
        return negatives;
    }

    private static bool ProductMatches(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(ChunkResult r, BenchmarkQuery q)
    {
        if (q.ExpectProduct is not null
            && string.Equals(r.Product, q.ExpectProduct, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return q.ExpectAny.Any(token =>
            r.DocumentSlug.Contains(token, StringComparison.OrdinalIgnoreCase)
            || r.DocumentTitle.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static TripleEntry ToEntry(ChunkResult r) => new(
        DocumentId: r.DocumentId,
        DocumentSlug: r.DocumentSlug,
        DocumentTitle: r.DocumentTitle,
        Product: r.Product,
        ChunkId: r.ChunkId,
        PageStart: r.PageStart,
        PageEnd: r.PageEnd,
        Text: r.RawContent);

    public static async Task<int> WriteJsonlAsync(
        IReadOnlyList<TripleCandidate> triples,
        string outPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(triples);
        ArgumentException.ThrowIfNullOrEmpty(outPath);

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        await using var stream = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        await using var writer = new StreamWriter(stream);
        var count = 0;
        foreach (var t in triples)
        {
            ct.ThrowIfCancellationRequested();
            var line = JsonSerializer.Serialize(t, options);
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            count++;
        }
        await writer.FlushAsync(ct).ConfigureAwait(false);
        return count;
    }
}
