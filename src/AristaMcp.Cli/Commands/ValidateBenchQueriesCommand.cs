using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using AristaMcp.Cli.Configuration;
using AristaMcp.Core.Retrieval;
using AristaMcp.Core.Settings;
using AristaMcp.Data;
using AristaMcp.Embedding;
using AristaMcp.Server;
using AristaMcp.Server.Retrieval;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;

namespace AristaMcp.Cli.Commands;

// Sprint 13.3 — bench-queries fairness filter. Reads a JSONL of candidate
// queries produced by arista-reranker-tune/scripts/generate_bench_queries.py,
// runs each through the current HybridRetriever, and keeps queries whose
// source_chunk_id appears in the retriever's top-20. Dropped queries are
// too vague for the current stack to answer — including them in the bench
// would measure a different thing (retrieval recall) than what we want
// to measure (reranker tie-breaking).
//
// Also records the retriever's top-10 chunk IDs per surviving query so
// Sprint 13.4 can prompt an LLM to annotate multi-positive cases.
public static class ValidateBenchQueriesCommand
{
    public static Command Build()
    {
        var input = new Option<FileInfo>("--input")
        {
            Description = "Input JSONL (one query per line with source_chunk_id)",
            Required = true,
        };
        var output = new Option<FileInfo>("--output")
        {
            Description = "Output JSONL — kept queries enriched with retriever top-10",
            Required = true,
        };
        var topK = new Option<int>("--top-k")
        {
            Description = "Keep queries where source_chunk_id is in top-K retrieval (default 20)",
            DefaultValueFactory = _ => 20,
        };
        var models = new Option<DirectoryInfo>("--models") { Description = "Models directory override" };

        var cmd = new Command("validate-bench-queries",
            "Filter LLM-generated bench queries by current retriever top-K reachability")
        {
            input, output, topK, models,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            return await RunAsync(
                pr.GetValue(input)!.FullName,
                pr.GetValue(output)!.FullName,
                pr.GetValue(topK),
                pr.GetValue(models),
                ct).ConfigureAwait(false);
        });

        return cmd;
    }

    private static async Task<int> RunAsync(
        string inputPath,
        string outputPath,
        int topK,
        DirectoryInfo? modelsOverride,
        CancellationToken ct)
    {
        var console = AnsiConsole.Console;
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

        if (!File.Exists(inputPath))
        {
            console.MarkupLine($"[red]error[/] input file not found: {Markup.Escape(inputPath)}");
            return 2;
        }

        // Read candidates BEFORE constructing the embedder + reranker — ONNX
        // session init is ~200 ms and pointless on empty input. This also
        // closes M6 from the v0.2.0 code review.
        var candidates = new List<QueryRecord>();
        using (var reader = File.OpenText(inputPath))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var rec = JsonSerializer.Deserialize(line, BenchQueryJsonContext.Default.QueryRecord);
                if (rec is not null)
                {
                    candidates.Add(rec);
                }
            }
        }

        if (candidates.Count == 0)
        {
            console.MarkupLine($"[red]error[/] no candidate queries parsed from {Markup.Escape(inputPath)} (empty or malformed JSONL)");
            return 2;
        }

        console.MarkupLine($"[bold]validate-bench-queries[/] — {candidates.Count} candidates from {Markup.Escape(inputPath)}");

        await using var ds = DataSourceFactory.Build(settings.ConnectionString);

        using var embedder = new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = embedderModel,
            VocabPath = embedderVocab,
            Gpu = settings.Gpu,
        });
        using IReranker reranker = BuildReranker(modelsDir, settings.Gpu);
        var hyde = ServerHosting.BuildHyde(settings);
        var multiQuery = ServerHosting.BuildMultiQuery(settings);
        var listwise = ServerHosting.BuildListwise(settings);
        var retriever = new HybridRetriever(embedder, reranker, ds, hyde, multiQuery, listwise);

        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var kept = 0;
        var dropped = 0;
        await using var writer = new StreamWriter(outputPath, append: false);

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var q = candidates[i];
            var resp = await retriever.SearchAsync(
                q.Query,
                new RetrievalOptions { Limit = topK, CandidatePoolSize = 50, RerankTopN = 30 },
                ct).ConfigureAwait(false);

            var top = resp.Results.Select(r => r.ChunkId).ToList();
            var found = top.Contains(q.SourceChunkId);
            if (!found)
            {
                dropped++;
                continue;
            }

            var enriched = new ValidatedQueryRecord(
                Query: q.Query,
                SourceChunkId: q.SourceChunkId,
                SourceProduct: q.SourceProduct,
                SourceDocTitle: q.SourceDocTitle,
                SourceSectionTitle: q.SourceSectionTitle,
                GenerationModel: q.GenerationModel,
                RetrieverRank: top.IndexOf(q.SourceChunkId),
                RetrieverTop10ChunkIds: [.. top.Take(10)]);
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(enriched, BenchQueryJsonContext.Default.ValidatedQueryRecord)).ConfigureAwait(false);
            kept++;

            if ((i + 1) % 25 == 0)
            {
                console.MarkupLine($"  [dim]{i + 1}/{candidates.Count}[/] kept={kept} dropped={dropped}");
            }
        }

        console.MarkupLine("[bold]summary[/]");
        console.MarkupLine($"  candidates                {candidates.Count}");
        console.MarkupLine($"  kept (source in top-{topK})   {kept}");
        console.MarkupLine($"  dropped                   {dropped}");
        console.MarkupLine($"  survival rate             {(kept * 100.0 / Math.Max(1, candidates.Count)):F1}%");
        console.MarkupLine($"  output                    {Markup.Escape(outputPath)}");
        return kept > 0 ? 0 : 1;
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
}

