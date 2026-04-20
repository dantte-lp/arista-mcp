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
        var docCount = await db.Documents.CountAsync(ct).ConfigureAwait(false);
        var chunkCount = await db.Chunks.CountAsync(ct).ConfigureAwait(false);
        var lastRun = await runRepo.GetLastAsync(ct).ConfigureAwait(false);

        return new
        {
            healthy = true,
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
