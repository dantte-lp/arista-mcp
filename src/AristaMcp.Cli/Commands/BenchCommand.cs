using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using AristaMcp.Cli.Benchmarks;
using AristaMcp.Cli.Configuration;
using AristaMcp.Core.Retrieval;
using AristaMcp.Core.Settings;
using AristaMcp.Server;
using AristaMcp.Server.Observability;
using AristaMcp.Data;
using AristaMcp.Embedding;
using AristaMcp.Server.Retrieval;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;

namespace AristaMcp.Cli.Commands;

public static class BenchCommand
{
    public static Command Build()
    {
        var queries = new Option<FileInfo>("--queries")
        {
            Description = "Path to the benchmark query JSON (defaults to tests/fixtures/bench-queries.json)",
        };
        var limit = new Option<int>("--limit") { Description = "Top-K per query (default 10)", DefaultValueFactory = _ => 10 };
        var models = new Option<DirectoryInfo>("--models") { Description = "Models directory override" };
        var history = new Option<FileInfo>("--history")
        {
            Description = "Append a JSONL row per run to this path (date, counts, p50/p95/avg)",
        };
        var label = new Option<string>("--label")
        {
            Description = "Free-form label for the history row (e.g. 'v0.1.1-baseline')",
        };

        var cmd = new Command("bench", "Run a retrieval benchmark against the ingested corpus")
        {
            queries,
            limit,
            models,
            history,
            label,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var queriesPath = ResolveQueriesPath(pr.GetValue(queries));
            var topK = Math.Clamp(pr.GetValue(limit), 1, 50);
            return await RunAsync(
                queriesPath,
                topK,
                pr.GetValue(models),
                pr.GetValue(history),
                pr.GetValue(label),
                ct).ConfigureAwait(false);
        });

        return cmd;
    }

    private static string ResolveQueriesPath(FileInfo? arg)
    {
        if (arg is not null)
        {
            return arg.FullName;
        }

        var candidate = Path.GetFullPath(
            Path.Combine(Environment.CurrentDirectory, "tests", "fixtures", "bench-queries.json"));
        return candidate;
    }

    private static async Task<int> RunAsync(
        string queriesPath,
        int topK,
        DirectoryInfo? modelsOverride,
        FileInfo? historyPath,
        string? label,
        CancellationToken ct)
    {
        // Dispose flushes the batch OTLP exporter before the CLI exits —
        // crucial for a short-lived command that would otherwise drop the
        // last few seconds of spans. No-op when OTEL isn't enabled.
        using var otel = OtelConfig.BuildTracerProviderIfEnabled();

        var console = AnsiConsole.Console;
        if (!File.Exists(queriesPath))
        {
            console.MarkupLine($"[red]error[/] query set not found at {Markup.Escape(queriesPath)}");
            return 2;
        }

        var settings = CliConfiguration.Load();
        if (modelsOverride is not null)
        {
            settings.ModelsDir = modelsOverride.FullName;
        }
        var modelsDir = settings.ModelsDir;
        var embedderModel = ModelPaths.EmbedderModel(settings);
        var embedderVocab = ModelPaths.EmbedderVocab(settings);
        if (!File.Exists(embedderModel) || !File.Exists(embedderVocab))
        {
            console.MarkupLine($"[red]error[/] embedder model missing at {Markup.Escape(modelsDir)}/embedder");
            return 2;
        }

        var json = await File.ReadAllTextAsync(queriesPath, ct).ConfigureAwait(false);
        var set = JsonSerializer.Deserialize<BenchmarkQuerySet>(json)
            ?? throw new InvalidOperationException($"Failed to deserialize {queriesPath}");

        console.MarkupLine($"[bold]arista-mcp bench[/] — {set.Queries.Count} queries, top-{topK}");

        await using var ds = DataSourceFactory.Build(settings.ConnectionString);
        var dbOpts = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(ds, o => o.UseVector())
            .Options;
        await using var ctx = new AristaDbContext(dbOpts);

        using var embedder = new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = embedderModel,
            VocabPath = embedderVocab,
            Gpu = settings.Gpu,
        });

        using IReranker reranker = BuildReranker(modelsDir, settings.Gpu);
        var hyde = ServerHosting.BuildHyde(settings);
        var retriever = new HybridRetriever(embedder, reranker, ds, hyde);

        var rows = new List<BenchRow>(set.Queries.Count);

        foreach (var q in set.Queries)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var resp = await retriever.SearchAsync(
                q.Query,
                new RetrievalOptions { Limit = topK, CandidatePoolSize = 50, RerankTopN = 30 },
                ct).ConfigureAwait(false);
            sw.Stop();

            var hitTop1 = resp.Results.Count > 0 && ResultMatches(resp.Results[0], q);
            var hitTopK = resp.Results.Any(r => ResultMatches(r, q));

            rows.Add(new BenchRow(q.Query, hitTop1, hitTopK, sw.Elapsed.TotalMilliseconds));
        }

        RenderTable(console, rows, topK);
        RenderSummary(console, rows);

        if (historyPath is not null)
        {
            await AppendHistoryAsync(historyPath.FullName, rows, topK, label, set.Version, ct).ConfigureAwait(false);
            console.MarkupLine($"  [grey]history → {Markup.Escape(historyPath.FullName)}[/]");
        }

        var hitRate = rows.Count == 0 ? 0.0 : rows.Count(r => r.Top10) * 100.0 / rows.Count;
        return hitRate >= 80 ? 0 : 1;
    }

    private static Task AppendHistoryAsync(
        string path,
        List<BenchRow> rows,
        int topK,
        string? label,
        int querySetVersion,
        CancellationToken ct)
    {
        var latencies = rows.Select(r => r.ElapsedMs).OrderBy(x => x).ToArray();
        var entry = new
        {
            date = DateTimeOffset.UtcNow,
            label,
            query_set_version = querySetVersion,
            query_count = rows.Count,
            top_k = topK,
            top1_hit_rate = rows.Count == 0 ? 0.0 : Math.Round(rows.Count(r => r.Top1) * 100.0 / rows.Count, 2),
            topk_hit_rate = rows.Count == 0 ? 0.0 : Math.Round(rows.Count(r => r.Top10) * 100.0 / rows.Count, 2),
            latency_p50_ms = latencies.Length > 0 ? Math.Round(latencies[latencies.Length / 2], 1) : 0,
            latency_p95_ms = latencies.Length > 0 ? Math.Round(latencies[(int)Math.Min(latencies.Length - 1, Math.Ceiling(latencies.Length * 0.95) - 1)], 1) : 0,
            latency_avg_ms = latencies.Length > 0 ? Math.Round(latencies.Average(), 1) : 0,
        };

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        return File.AppendAllTextAsync(path, line, ct);
    }

    private static IReranker BuildReranker(string modelsDir, bool gpu)
    {
        var family = RerankerFamilyDetector.Detect(modelsDir);
        var modelPath = ModelPaths.RerankerModel(modelsDir);
        return family switch
        {
            RerankerTokenizerFamily.XlmRobertaSentencePiece => new XlmRobertaOnnxReranker(new RerankerOptions
            {
                ModelPath = modelPath,
                VocabPath = ModelPaths.RerankerSpm(modelsDir),
                Gpu = gpu,
            }),
            RerankerTokenizerFamily.BertWordPiece => new OnnxReranker(new RerankerOptions
            {
                ModelPath = modelPath,
                VocabPath = ModelPaths.RerankerVocab(modelsDir),
                Gpu = gpu,
            }),
            _ => new NoopReranker(),
        };
    }

    private static bool ResultMatches(Core.Models.ChunkResult r, Benchmarks.BenchmarkQuery query)
    {
        // v2 bench takes precedence: if ground-truth chunk IDs are populated,
        // scoring is pure ID membership and the heuristic fields are ignored.
        if (query.ExpectAnyOfChunkIds.Count > 0)
        {
            return query.ExpectAnyOfChunkIds.Contains(r.ChunkId);
        }

        if (query.ExpectProduct is not null
            && string.Equals(r.Product, query.ExpectProduct, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (query.ExpectAny.Count == 0)
        {
            return false;
        }

        return query.ExpectAny.Any(token =>
            r.DocumentSlug.Contains(token, StringComparison.OrdinalIgnoreCase)
            || r.DocumentTitle.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static void RenderTable(IAnsiConsole console, List<BenchRow> rows, int topK)
    {
        var table = new Table().RoundedBorder();
        table.AddColumn("query");
        table.AddColumn(new TableColumn("top1").Centered());
        table.AddColumn(new TableColumn($"top{topK}").Centered());
        table.AddColumn(new TableColumn("ms").RightAligned());

        foreach (var r in rows)
        {
            table.AddRow(
                Markup.Escape(r.Query),
                r.Top1 ? "[green]✓[/]" : "[red]✗[/]",
                r.Top10 ? "[green]✓[/]" : "[red]✗[/]",
                $"{r.ElapsedMs:F0}");
        }

        console.Write(table);
    }

    private static void RenderSummary(IAnsiConsole console, List<BenchRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var latencies = rows.Select(r => r.ElapsedMs).OrderBy(x => x).ToArray();
        var p50 = latencies[latencies.Length / 2];
        var p95 = latencies[(int)Math.Min(latencies.Length - 1, Math.Ceiling(latencies.Length * 0.95) - 1)];
        var avg = latencies.Average();
        var top1 = rows.Count(r => r.Top1) * 100.0 / rows.Count;
        var topK = rows.Count(r => r.Top10) * 100.0 / rows.Count;

        console.MarkupLine($"[bold]summary[/]");
        console.MarkupLine($"  top-1 hit rate  {top1:F1}%");
        console.MarkupLine($"  top-K hit rate  {topK:F1}%");
        console.MarkupLine($"  latency p50     {p50:F0} ms");
        console.MarkupLine($"  latency p95     {p95:F0} ms");
        console.MarkupLine($"  latency avg     {avg:F0} ms");
    }

    private sealed record BenchRow(string Query, bool Top1, bool Top10, double ElapsedMs);
}
