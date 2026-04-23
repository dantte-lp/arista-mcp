using System.CommandLine;
using System.Text.Json;
using AristaMcp.Cli.Benchmarks;
using AristaMcp.Cli.Configuration;
using AristaMcp.Cli.Curation;
using AristaMcp.Core.Retrieval;
using AristaMcp.Core.Settings;
using AristaMcp.Data;
using AristaMcp.Embedding;
using AristaMcp.Server.Observability;
using AristaMcp.Server.Retrieval;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;

namespace AristaMcp.Cli.Commands;

// Emits (query, positive, hard-negatives) JSONL for cross-encoder reranker
// fine-tuning (Sprint 9 prerequisite). Retrieval stack mirrors `bench`:
// same embedder + reranker + DataSource wiring. Hard negatives are sourced
// from top-20 HybridRetriever hits that are (a) not the positive's chunk,
// (b) not a sibling chunk of the positive's doc, and (c) from a different
// product — plausible-but-wrong, which is what margin loss needs.
public static class CurateTriplesCommand
{
    public static Command Build()
    {
        var queries = new Option<FileInfo>("--queries")
        {
            Description = "Path to the benchmark query JSON (defaults to tests/fixtures/bench-queries.json)",
        };
        var output = new Option<FileInfo>("--out")
        {
            Description = "Output JSONL path (default: tests/fixtures/reranker-triples.jsonl)",
        };
        var negatives = new Option<int>("--negatives-per-query")
        {
            Description = "Hard negatives per query (default 4)",
            DefaultValueFactory = _ => 4,
        };
        var models = new Option<DirectoryInfo>("--models") { Description = "Models directory override" };

        var cmd = new Command("curate-triples", "Generate (query, positive, negatives) JSONL for reranker fine-tune")
        {
            queries,
            output,
            negatives,
            models,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            return await RunAsync(
                ResolveQueriesPath(pr.GetValue(queries)),
                ResolveOutPath(pr.GetValue(output)),
                Math.Max(1, pr.GetValue(negatives)),
                pr.GetValue(models),
                ct).ConfigureAwait(false);
        });

        return cmd;
    }

    private static string ResolveQueriesPath(FileInfo? arg) =>
        arg?.FullName ?? Path.GetFullPath(
            Path.Combine(Environment.CurrentDirectory, "tests", "fixtures", "bench-queries.json"));

    private static string ResolveOutPath(FileInfo? arg) =>
        arg?.FullName ?? Path.GetFullPath(
            Path.Combine(Environment.CurrentDirectory, "tests", "fixtures", "reranker-triples.jsonl"));

    private static async Task<int> RunAsync(
        string queriesPath,
        string outPath,
        int negativesPerQuery,
        DirectoryInfo? modelsOverride,
        CancellationToken ct)
    {
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

        console.MarkupLine(
            $"[bold]arista-mcp curate-triples[/] — {set.Queries.Count} queries, "
            + $"{negativesPerQuery} negatives each");

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
        var retriever = new HybridRetriever(embedder, reranker, ds);

        var (triples, stats) = await TripleCurator.CurateAsync(
            set.Queries, retriever, negativesPerQuery, ct).ConfigureAwait(false);

        var written = await TripleCurator.WriteJsonlAsync(triples, outPath, ct).ConfigureAwait(false);

        console.MarkupLine("[bold]summary[/]");
        console.MarkupLine($"  queries total                     {stats.QueriesTotal}");
        console.MarkupLine($"  queries with positive             {stats.QueriesWithPositive}");
        console.MarkupLine($"  queries skipped (no positive)     {stats.QueriesSkippedNoPositive}");
        console.MarkupLine($"  queries skipped (few negatives)   {stats.QueriesSkippedInsufficientNegatives}");
        console.MarkupLine($"  triples emitted                   {stats.TriplesEmitted}");
        console.MarkupLine($"  output                            {Markup.Escape(outPath)}");

        return written >= 1 ? 0 : 1;
    }

    private static IReranker BuildReranker(string modelsDir, bool gpu)
    {
        var modelPath = Path.Combine(modelsDir, "reranker", "model.onnx");
        var vocabPath = Path.Combine(modelsDir, "reranker", "vocab.txt");
        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            return new NoopReranker();
        }

        return new OnnxReranker(new RerankerOptions
        {
            ModelPath = modelPath,
            VocabPath = vocabPath,
            Gpu = gpu,
        });
    }
}
