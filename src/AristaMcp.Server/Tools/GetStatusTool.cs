using System.ComponentModel;
using AristaMcp.Data;
using AristaMcp.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace AristaMcp.Server.Tools;

[McpServerToolType]
public sealed class GetStatusTool(AristaDbContext db, IIngestRunRepository runRepo)
{
    [McpServerTool(Name = "get_status")]
    [Description("Health and store stats: document/chunk counts, last ingest run, extension versions.")]
    public async Task<object> GetAsync(CancellationToken ct = default)
    {
        // Derive health from a real probe rather than asserting it. A DB that is
        // unreachable, or a store with zero documents/chunks, is not healthy —
        // reporting healthy=true unconditionally hid exactly those failures. On a
        // connectivity error, report the store as unhealthy instead of throwing so
        // the client still gets a structured status payload.
        int docCount;
        int chunkCount;
        Data.Entities.IngestRunEntity? lastRun;
        try
        {
            docCount = await db.Documents.CountAsync(ct).ConfigureAwait(false);
            chunkCount = await db.Chunks.CountAsync(ct).ConfigureAwait(false);
            lastRun = await runRepo.GetLastAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new
            {
                healthy = false,
                documents = 0,
                chunks = 0,
                error = ex.Message,
                last_run = (object?)null,
            };
        }

        var healthy = docCount > 0 && chunkCount > 0;

        return new
        {
            healthy,
            documents = docCount,
            chunks = chunkCount,
            last_run = lastRun is null ? null : new
            {
                id = lastRun.Id,
                status = lastRun.Status,
                started_at = lastRun.StartedAt,
                finished_at = lastRun.FinishedAt,
                docs_total = lastRun.DocsTotal,
                docs_skipped = lastRun.DocsSkipped,
                docs_upserted = lastRun.DocsUpserted,
                chunks_upserted = lastRun.ChunksUpserted,
                error = lastRun.ErrorMsg,
            },
        };
    }
}
