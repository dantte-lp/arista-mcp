using System.CommandLine;
using AristaMcp.Cli.Configuration;
using AristaMcp.Cli.Ingest;
using AristaMcp.Cli.Progress;
using AristaMcp.Core.Chunking;
using AristaMcp.Core.Settings;
using AristaMcp.Data;
using AristaMcp.Data.Repositories;
using AristaMcp.Embedding;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;

namespace AristaMcp.Cli.Commands;

public static class IngestCommand
{
    public static Command Build()
    {
        var catalog = new Option<FileInfo>("--catalog")
        {
            Description = "Path to arista-docs/data/catalog.json (defaults to ../arista-docs/data/catalog.json)",
        };
        var force = new Option<bool>("--force") { Description = "Re-ingest even if the catalog SHA matches the last run" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Parse + chunk, but skip embed + database writes" };
        var category = new Option<string>("--category") { Description = "Filter to one category (e.g. 'toi', 'manual')" };
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Log per-document progress" };
        var models = new Option<DirectoryInfo>("--models")
        {
            Description = "Override models directory (defaults to ./models or ARISTA_MCP__ModelsDir)",
        };

        var cmd = new Command("ingest", "Ingest arista-docs catalog into the pgvector store")
        {
            catalog,
            force,
            dryRun,
            category,
            verbose,
            models,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var opts = new IngestOptions
            {
                CatalogPath = ResolveCatalogPath(pr.GetValue(catalog)),
                Force = pr.GetValue(force),
                DryRun = pr.GetValue(dryRun),
                Category = pr.GetValue(category),
                Verbose = pr.GetValue(verbose),
            };

            return await RunAsync(opts, pr.GetValue(models), ct).ConfigureAwait(false);
        });

        return cmd;
    }

    private static string ResolveCatalogPath(FileInfo? arg)
    {
        if (arg is not null)
        {
            return arg.FullName;
        }

        var candidate = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "arista-docs", "data", "catalog.json"));
        return candidate;
    }

    private static async Task<int> RunAsync(IngestOptions opts, DirectoryInfo? modelsOverride, CancellationToken ct)
    {
        var settings = CliConfiguration.Load();
        if (modelsOverride is not null)
        {
            settings.ModelsDir = modelsOverride.FullName;
        }
        var modelsDir = settings.ModelsDir;

        var console = AnsiConsole.Console;
        console.MarkupLine($"[bold]arista-mcp ingest[/]");
        console.MarkupLine($"  catalog   [grey]{Markup.Escape(opts.CatalogPath)}[/]");
        console.MarkupLine($"  models    [grey]{Markup.Escape(modelsDir)}[/]");
        console.MarkupLine($"  dry-run   {opts.DryRun}");
        console.MarkupLine($"  force     {opts.Force}");
        if (opts.Category is not null)
        {
            console.MarkupLine($"  category  [grey]{Markup.Escape(opts.Category)}[/]");
        }

        var modelPath = ModelPaths.EmbedderModel(settings);
        var vocabPath = ModelPaths.EmbedderVocab(settings);

        if (!opts.DryRun && (!File.Exists(modelPath) || !File.Exists(vocabPath)))
        {
            console.MarkupLine($"[red]error[/] embedder model/vocab missing at {Markup.Escape(modelsDir)}/embedder — run scripts/fetch-models.ps1");
            return 2;
        }

        await using var dataSource = DataSourceFactory.Build(settings.ConnectionString);

        var dbOpts = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(dataSource, o => o.UseVector())
            .Options;
        await using var ctx = new AristaDbContext(dbOpts);

        var chunker = new SectionAwareChunker(new ChunkingOptions
        {
            TargetTokens = settings.ChunkTargetTokens,
            MaxTokens = settings.ChunkMaxTokens,
            OverlapTokens = settings.ChunkOverlapTokens,
            MinTokens = settings.ChunkMinTokens,
        });

        using IEmbedder embedder = opts.DryRun
            ? new NoopEmbedder()
            : new OnnxEmbedder(new EmbeddingOptions
            {
                ModelPath = modelPath,
                VocabPath = vocabPath,
                BatchSize = settings.IngestBatchSize,
                Gpu = settings.Gpu,
            });

        var service = new IngestService(
            chunker,
            embedder,
            new DocumentRepository(ctx, TimeProvider.System),
            new ChunkRepository(dataSource, ctx),
            new IngestRunRepository(ctx, TimeProvider.System));

        var progress = new SpectreIngestProgress(console, opts.Verbose);
        var result = await service.IngestAsync(opts, progress, ct).ConfigureAwait(false);

        console.MarkupLine($"[bold]result[/] status=[yellow]{result.Status}[/] total={result.DocsTotal} skipped={result.DocsSkipped} upserted={result.DocsUpserted} chunks={result.ChunksUpserted}");
        if (result.Error is not null)
        {
            console.MarkupLine($"[red]error[/] {Markup.Escape(result.Error)}");
        }

        return result.Status is "success" or "skipped" ? 0 : 1;
    }

    private sealed class NoopEmbedder : IEmbedder
    {
        public int Dimension => 768;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct) =>
            Task.FromResult(new float[texts.Count][]);

        public void Dispose() { }
    }
}
