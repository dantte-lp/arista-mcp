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

        services.AddSingleton<NpgsqlDataSource>(_ => DataSourceFactory.Build(settings.ConnectionString));

        services.AddDbContext<AristaDbContext>((sp, o) =>
        {
            var ds = sp.GetRequiredService<NpgsqlDataSource>();
            o.UseNpgsql(ds, n => n.UseVector());
        });

        services.AddSingleton<IEmbedder>(_ => BuildEmbedder(settings));
        services.AddSingleton<IReranker>(_ => new NoopReranker());

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

    private static OnnxEmbedder BuildEmbedder(AristaMcpSettings settings)
    {
        var modelPath = Path.Combine(settings.ModelsDir, "embedder", "model.onnx");
        var vocabPath = Path.Combine(settings.ModelsDir, "embedder", "vocab.txt");
        return new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = modelPath,
            VocabPath = vocabPath,
            Gpu = settings.Gpu,
        });
    }
}
