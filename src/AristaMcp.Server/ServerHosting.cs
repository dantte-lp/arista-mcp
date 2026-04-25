using AristaMcp.Core.Retrieval;
using AristaMcp.Core.Settings;
using AristaMcp.Data;
using AristaMcp.Data.Repositories;
using AristaMcp.Embedding;
using AristaMcp.Server.Observability;
using AristaMcp.Server.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AristaMcp.Server;

public static class ServerHosting
{
    // Registers the shared DI surface used by both the stdio and HTTP server hosts.
    // Keep this the single source of truth for service lifetimes so the two transports
    // never drift on, e.g., whether the embedder is a singleton (it must be — ONNX
    // session startup is expensive).
    public static IServiceCollection AddAristaMcpServices(
        this IServiceCollection services,
        AristaMcpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<NpgsqlDataSource>(_ => DataSourceFactory.Build(settings.ConnectionString));

        services.AddDbContext<AristaDbContext>((sp, o) =>
        {
            var ds = sp.GetRequiredService<NpgsqlDataSource>();
            o.UseNpgsql(ds, n => n.UseVector());
        });

        services.AddSingleton<IEmbedder>(_ => BuildAndWarmEmbedder(settings));
        services.AddSingleton<IReranker>(_ => BuildReranker(settings));
        services.AddSingleton<IHydeExpander>(_ => BuildHyde(settings));
        services.AddSingleton<IMultiQueryExpander>(_ => BuildMultiQuery(settings));

        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IChunkRepository>(sp => new ChunkRepository(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<AristaDbContext>()));
        services.AddScoped<IIngestRunRepository, IngestRunRepository>();

        services.AddSingleton<IHybridRetriever>(sp => new HybridRetriever(
            sp.GetRequiredService<IEmbedder>(),
            sp.GetRequiredService<IReranker>(),
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<IHydeExpander>(),
            sp.GetRequiredService<IMultiQueryExpander>()));

        // Opt-in: registers OpenTelemetry tracing + OTLP exporter IF any of
        // ARISTA_MCP__Otel__Endpoint or OTEL_EXPORTER_OTLP_ENDPOINT is set.
        // Otherwise a no-op — zero allocations, zero exporter threads.
        services.AddOtelIfEnabled();

        return services;
    }

    // Detect tokenizer family from files under models/reranker. If SPM is
    // present pick the XLM-R path for bge-reranker; else if vocab.txt is
    // present pick the BERT WordPiece path for MiniLM; else fall back to
    // NoopReranker so serve stays usable with no reranker asset installed.
    private static IReranker BuildReranker(AristaMcpSettings settings)
    {
        var family = RerankerFamilyDetector.Detect(settings.ModelsDir);
        var modelPath = ModelPaths.RerankerModel(settings.ModelsDir);

        return family switch
        {
            RerankerTokenizerFamily.XlmRobertaSentencePiece => new XlmRobertaOnnxReranker(new RerankerOptions
            {
                ModelPath = modelPath,
                VocabPath = ModelPaths.RerankerSpm(settings.ModelsDir),
                Gpu = settings.Gpu,
            }),
            RerankerTokenizerFamily.BertWordPiece => new OnnxReranker(new RerankerOptions
            {
                ModelPath = modelPath,
                VocabPath = ModelPaths.RerankerVocab(settings.ModelsDir),
                Gpu = settings.Gpu,
            }),
            _ => new NoopReranker(),
        };
    }

    // HyDE expander factory. Off by default (NoopHydeExpander) so startups
    // without the llama.cpp sidecar running don't burn time on DNS lookups
    // for a localhost that isn't listening. HTTP client timeout is slightly
    // larger than the per-request timeout because PostAsJsonAsync also runs
    // the connection handshake under the same budget.
    public static IHydeExpander BuildHyde(AristaMcpSettings settings)
    {
        if (!settings.Hyde.Enabled)
        {
            return new NoopHydeExpander();
        }

        var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(settings.Hyde.TimeoutMs + 1000),
        };
        return new HydeExpander(http, settings.Hyde, TimeProvider.System);
    }

    // Multi-query expander factory. Off by default until the v0.2.5 bench
    // gates confirm no top-K regression on niche queries.
    public static IMultiQueryExpander BuildMultiQuery(AristaMcpSettings settings)
    {
        return settings.MultiQuery.Enabled
            ? new MultiQueryExpander()
            : new NoopMultiQueryExpander();
    }

    private static OnnxEmbedder BuildEmbedder(AristaMcpSettings settings)
    {
        return new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = ModelPaths.EmbedderModel(settings),
            VocabPath = ModelPaths.EmbedderVocab(settings),
            Gpu = settings.Gpu,
        });
    }

    // Sprint 8.4d: warm the ONNX session during DI resolution so the first real
    // request doesn't pay the ~200 ms graph-init cost. Blocking .GetAwaiter().
    // GetResult() is intentional — the container expects AddSingleton factories
    // to return eagerly, and the cost is a one-time hit during host startup.
    private static OnnxEmbedder BuildAndWarmEmbedder(AristaMcpSettings settings)
    {
        var embedder = BuildEmbedder(settings);
        try
        {
            _ = embedder.EmbedAsync(["warmup"], isQuery: false, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Warm-up failure should not block host startup — the real request
            // will surface whatever underlying error this hit (model missing,
            // GPU disabled, etc.) with a proper exception path. Swallowing
            // here keeps a malformed environment from looking "hung".
            // OOM/stack overflow still propagate — those are not recoverable.
            System.Diagnostics.Debug.WriteLine(
                $"[arista-mcp] embedder warm-up failed (non-fatal): {ex.Message}");
        }
        return embedder;
    }
}
