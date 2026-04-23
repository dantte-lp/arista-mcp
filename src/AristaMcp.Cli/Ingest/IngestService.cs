using AristaMcp.Core.Catalog;
using AristaMcp.Core.Chunking;
using AristaMcp.Core.Models;
using AristaMcp.Data.Repositories;
using AristaMcp.Embedding;

namespace AristaMcp.Cli.Ingest;

// End-to-end ingest orchestrator: catalog → loader → chunker → embedder → repositories.
// Tracks each run in ingest_runs. Incremental skip works in two layers:
//   • catalog SHA256 matches the last successful run → short-circuits entirely
//   • per-doc: pdf_sha256 matches the stored value → that doc is skipped
// Per-doc failures are counted but do not abort the run; the run ends as 'partial'
// if any doc errors, 'success' if everything upserts cleanly.
public sealed class IngestService(
    IChunker chunker,
    IEmbedder embedder,
    IDocumentRepository docRepo,
    IChunkRepository chunkRepo,
    IIngestRunRepository runRepo)
{
    public async Task<IngestResult> IngestAsync(
        IngestOptions options,
        IIngestProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        var catalog = await CatalogReader.ReadAsync(options.CatalogPath, ct).ConfigureAwait(false);
        var run = await runRepo.StartAsync(catalog.Sha256, ct).ConfigureAwait(false);

        if (!options.Force)
        {
            var lastSha = await runRepo.GetLastSuccessfulCatalogSha256Async(ct).ConfigureAwait(false);
            if (lastSha is not null && string.Equals(lastSha, catalog.Sha256, StringComparison.Ordinal))
            {
                progress.Log($"catalog unchanged since last successful run (sha={catalog.Sha256[..12]}…); skipping.");
                await runRepo.FinishAsync(run.Id, "skipped", 0, 0, 0, 0, null, ct).ConfigureAwait(false);
                return new IngestResult("skipped", 0, 0, 0, 0, null);
            }
        }

        IReadOnlyList<CatalogEntry> filtered = options.Category is null
            ? catalog.Document.Documents
            : [.. catalog.Document.Documents.Where(
                d => string.Equals(d.Category, options.Category, StringComparison.OrdinalIgnoreCase))];

        var total = filtered.Count;
        var skipped = 0;
        var upserted = 0;
        var chunksUpserted = 0;
        string? error = null;

        try
        {
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = filtered[i];
                progress.BeginDocument(entry.Id, entry.Title, i + 1, total);

                var storedSha = await docRepo.GetPdfSha256Async(entry.Id, ct).ConfigureAwait(false);
                if (!options.Force
                    && storedSha is not null
                    && entry.PdfSha256 is not null
                    && string.Equals(storedSha, entry.PdfSha256, StringComparison.Ordinal))
                {
                    skipped++;
                    progress.EndDocument(entry.Id, 0, skipped: true);
                    continue;
                }

                var (chunkCount, docError) = await IngestDocumentAsync(
                    entry, catalog.BaseDirectory, options, progress, ct)
                    .ConfigureAwait(false);

                if (docError is not null)
                {
                    error = docError;
                    progress.Log($"[error] {entry.Id}: {docError}");
                    progress.EndDocument(entry.Id, 0, skipped: false);
                    continue;
                }

                upserted++;
                chunksUpserted += chunkCount;
                progress.EndDocument(entry.Id, chunkCount, skipped: false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }

        var status = DetermineStatus(error, upserted);
        await runRepo.FinishAsync(run.Id, status, total, skipped, upserted, chunksUpserted, error, ct)
            .ConfigureAwait(false);

        return new IngestResult(status, total, skipped, upserted, chunksUpserted, error);
    }

    private static string DetermineStatus(string? error, int upserted)
    {
        if (error is null)
        {
            return "success";
        }

        return upserted > 0 ? "partial" : "error";
    }

    private async Task<(int Chunks, string? Error)> IngestDocumentAsync(
        CatalogEntry entry,
        string catalogBaseDir,
        IngestOptions options,
        IIngestProgress progress,
        CancellationToken ct)
    {
        try
        {
            var loaded = await DocumentLoader.LoadAsync(entry, catalogBaseDir, ct).ConfigureAwait(false);
            if (loaded.Sections.Count == 0)
            {
                return (0, "no sections extracted");
            }

            var drafts = chunker.Chunk(loaded.Metadata.Title, loaded.Sections);
            if (drafts.Count == 0)
            {
                return (0, "no chunks produced");
            }

            if (options.DryRun)
            {
                return (drafts.Count, null);
            }

            // doc metadata + chunk wipe happen once — the sub-batch loop below
            // only inserts. If a later sub-batch fails we'll leave a doc with
            // partial chunks; the next `ingest --force` (or an unchanged sha
            // re-run after fix) will hit DeleteByDocument again and restart.
            await docRepo.UpsertAsync(loaded.Metadata, ct).ConfigureAwait(false);
            await chunkRepo.DeleteByDocumentAsync(entry.Id, ct).ConfigureAwait(false);

            var subBatchSize = Math.Max(1, options.ChunkSubBatchSize);
            var totalDrafts = drafts.Count;
            var inserted = 0;
            for (var start = 0; start < totalDrafts; start += subBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var end = Math.Min(start + subBatchSize, totalDrafts);
                var slice = new List<ChunkDraft>(end - start);
                for (var k = start; k < end; k++)
                {
                    slice.Add(drafts[k]);
                }

                var texts = slice.Select(d => d.Content).ToList();
                var vectors = await embedder.EmbedAsync(texts, isQuery: false, ct).ConfigureAwait(false);

                var chunks = new List<AristaChunk>(slice.Count);
                for (var j = 0; j < slice.Count; j++)
                {
                    var d = slice[j];
                    chunks.Add(new AristaChunk
                    {
                        DocumentId = entry.Id,
                        ChunkIndex = start + j,
                        Content = d.Content,
                        RawContent = d.RawContent,
                        SectionTitle = d.SectionTitle,
                        SectionLevel = d.SectionLevel,
                        PageStart = d.PageStart,
                        PageEnd = d.PageEnd,
                        TokenCount = d.TokenCount,
                        Embedding = vectors[j],
                    });
                }

                inserted += await chunkRepo.BulkInsertAsync(chunks, ct).ConfigureAwait(false);

                // Only surface sub-batch progress for docs that actually split.
                // Normal-sized docs log nothing extra — keeps Sprint 6 output
                // shape stable in the 99 % case.
                if (totalDrafts > subBatchSize)
                {
                    progress.Log(
                        $"  {entry.Slug}: sub-batch {inserted}/{totalDrafts} chunks");
                }
            }

            return (inserted, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, ex.Message);
        }
    }
}
