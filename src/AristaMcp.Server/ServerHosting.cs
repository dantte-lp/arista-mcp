using AristaMcp.Core.Retrieval;
using AristaMcp.Core.Settings;
using AristaMcp.Data;
using AristaMcp.Data.Repositories;
using AristaMcp.Embedding;
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

        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IChunkRepository>(sp => new ChunkRepository(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<AristaDbContext>()));
        services.AddScoped<IIngestRunRepository, IngestRunRepository>();

        services.AddSingleton<IHybridRetriever>(sp => new HybridRetriever(
            sp.GetRequiredService<IEmbedder>(),
            sp.GetRequiredService<IReranker>(),
            sp.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    // Swap to OnnxReranker when models/reranker/ is populated; otherwise fall back to
    // the passthrough. This keeps `arista-mcp serve` usable even without a rerank model.
    private static IReranker BuildReranker(AristaMcpSettings settings)
    {
        var modelPath = Path.Combine(settings.ModelsDir, "reranker", "model.onnx");
        var vocabPath = Path.Combine(settings.ModelsDir, "reranker", "vocab.txt");
        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            return new NoopReranker();
        }

        return new OnnxReranker(new RerankerOptions
        {
            ModelPath = modelPath,
            VocabPath = vocabPath,
            Gpu = settings.Gpu,
        });
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
