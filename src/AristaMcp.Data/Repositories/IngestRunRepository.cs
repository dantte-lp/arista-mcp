using AristaMcp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AristaMcp.Data.Repositories;

public class IngestRunRepository(AristaDbContext db, TimeProvider clock) : IIngestRunRepository
{
    public async Task<IngestRunEntity> StartAsync(string? catalogSha256, CancellationToken ct)
    {
        var entity = new IngestRunEntity
        {
            Status = "running",
            CatalogSha256 = catalogSha256,
            StartedAt = clock.GetUtcNow(),
        };
        db.IngestRuns.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity;
    }

    public async Task FinishAsync(
        long id,
        string status,
        int docsTotal,
        int docsSkipped,
        int docsUpserted,
        int chunksUpserted,
        string? errorMsg,
        CancellationToken ct)
    {
        var entity = await db.IngestRuns.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ingest_run {id} not found");

        entity.FinishedAt = clock.GetUtcNow();
        entity.Status = status;
        entity.DocsTotal = docsTotal;
        entity.DocsSkipped = docsSkipped;
        entity.DocsUpserted = docsUpserted;
        entity.ChunksUpserted = chunksUpserted;
        entity.ErrorMsg = errorMsg;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<IngestRunEntity?> GetLastAsync(CancellationToken ct) =>
        db.IngestRuns
            .AsNoTracking()
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(ct);

    public Task<string?> GetLastSuccessfulCatalogSha256Async(CancellationToken ct) =>
        db.IngestRuns
            .AsNoTracking()
            .Where(x => x.Status == "success" && x.CatalogSha256 != null)
            .OrderByDescending(x => x.StartedAt)
            .Select(x => x.CatalogSha256)
            .FirstOrDefaultAsync(ct);
}
