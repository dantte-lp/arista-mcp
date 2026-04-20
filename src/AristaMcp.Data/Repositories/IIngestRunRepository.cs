using AristaMcp.Data.Entities;

namespace AristaMcp.Data.Repositories;

public interface IIngestRunRepository
{
    Task<IngestRunEntity> StartAsync(string? catalogSha256, CancellationToken ct);

    Task FinishAsync(
        long id,
        string status,
        int docsTotal,
        int docsSkipped,
        int docsUpserted,
        int chunksUpserted,
        string? errorMsg,
        CancellationToken ct);

    Task<IngestRunEntity?> GetLastAsync(CancellationToken ct);

    Task<string?> GetLastSuccessfulCatalogSha256Async(CancellationToken ct);
}
