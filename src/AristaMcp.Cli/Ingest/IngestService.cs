using AristaMcp.Core.Catalog;
using AristaMcp.Core.Chunking;
using AristaMcp.Core.Models;
using AristaMcp.Core.Observability;
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
                    // Placeholder-filter trips are intentional skips, not errors —
                    // counting them toward `error` drags the final status to
                    // "partial" and exit code 1 even when every real doc
                    // ingested fine. The filter's message is a stable contract
                    // ("placeholder doc ... — skipped") so we match on that
                    // substring rather than introducing a new result shape.
                    if (docError.Contains("placeholder doc", StringComparison.Ordinal))
                    {
                        skipped++;
                        progress.Log($"[skip] {entry.Id}: {docError}");
                        progress.EndDocument(entry.Id, 0, skipped: true);
                        continue;
                    }

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
        using var docSpan = AristaActivity.Source.StartActivity(AristaActivity.Operations.IngestDocument);
        docSpan?.SetTag(AristaActivity.Tags.DocId, entry.Id);
        docSpan?.SetTag(AristaActivity.Tags.DocSlug, entry.Slug);

        try
        {
            var loaded = await DocumentLoader.LoadAsync(entry, catalogBaseDir, ct).ConfigureAwait(false);
            if (loaded.Sections.Count == 0)
            {
                return (0, "no sections extracted");
            }

            // Defensive: upstream arista-docs FakeConverter used to stamp
            // placeholder MD as accurate (fixed upstream; legacy catalogs may
            // still carry the lie). Placeholder bodies contain only a single
            // Fake-conversion heading plus a byte count, so chunking them
            // poisons BM25 with the literal phrase and wastes ~1 sec per doc
            // on ONNX inference. Two filters: a title prefix check and a
            // total-body minimum (40 chars; real fixtures land 60+).
            if (loaded.Sections.Count > 0
                && loaded.Sections[0].Title.StartsWith("Fake conversion of", StringComparison.Ordinal))
            {
                return (0, "placeholder doc (FakeConverter output) — skipped");
            }
            var totalBodyChars = 0;
            foreach (var s in loaded.Sections)
            {
                totalBodyChars += s.Content.Length;
            }
            if (totalBodyChars < 40)
            {
                return (0, $"placeholder doc ({totalBodyChars} body chars) — skipped");
            }

            var chunkSet = chunker.Chunk(loaded.Metadata.Title, loaded.Sections);
            var totalDrafts = chunkSet.Parents.Count + chunkSet.Leaves.Count;
            if (totalDrafts == 0)
            {
                return (0, "no chunks produced");
            }

            if (options.DryRun)
            {
                return (totalDrafts, null);
            }

            // doc metadata + chunk wipe happen once. The two-pass insert
            // below writes parents first (no embedder calls), reads back
            // their DB ids ordered by chunk_index, patches leaves with
            // parent_chunk_id, then runs the sub-batched embed +
            // BulkInsert loop on leaves.
            await docRepo.UpsertAsync(loaded.Metadata, ct).ConfigureAwait(false);
            await chunkRepo.DeleteByDocumentAsync(entry.Id, ct).ConfigureAwait(false);

            docSpan?.SetTag(AristaActivity.Tags.ChunkCount, totalDrafts);

            // Pass 1 — parents (no embedding). chunk_index = 0..ParentCount-1.
            var parentRows = new List<AristaChunk>(chunkSet.Parents.Count);
            for (var i = 0; i < chunkSet.Parents.Count; i++)
            {
                var p = chunkSet.Parents[i];
                parentRows.Add(new AristaChunk
                {
                    DocumentId = entry.Id,
                    ChunkIndex = i,
                    Content = p.Content,
                    RawContent = p.RawContent,
                    SectionTitle = p.SectionTitle,
                    SectionLevel = p.SectionLevel,
                    PageStart = p.PageStart,
                    PageEnd = p.PageEnd,
                    TokenCount = p.TokenCount,
                    ChunkKind = ChunkKind.Parent,
                    Embedding = null,
                    EmbeddingModel = null,
                });
            }
            await chunkRepo.BulkInsertAsync(parentRows, ct).ConfigureAwait(false);

            // Read parent ids back, ordered by chunk_index ascending — that
            // matches the order they were inserted, so parent index N from
            // the chunker maps to parentIds[N].
            var parentIds = await chunkRepo.SelectParentIdsAsync(entry.Id, ct).ConfigureAwait(false);
            if (parentIds.Count != chunkSet.Parents.Count)
            {
                return (0,
                    $"parent insert/readback mismatch: expected {chunkSet.Parents.Count}, got {parentIds.Count}");
            }

            // Pass 2 — leaves, embedded and inserted in sub-batches. chunk_index
            // continues from ParentCount so the (document_id, chunk_index)
            // unique constraint stays satisfied across the two passes.
            var subBatchSize = Math.Max(1, options.ChunkSubBatchSize);
            var leafCount = chunkSet.Leaves.Count;

            var inserted = parentRows.Count;
            var subBatchIndex = 0;
            var subBatchTotal = (leafCount + subBatchSize - 1) / subBatchSize;
            for (var start = 0; start < leafCount; start += subBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var end = Math.Min(start + subBatchSize, leafCount);
                using var subSpan = AristaActivity.Source.StartActivity(AristaActivity.Operations.IngestSubBatch);
                subSpan?.SetTag(AristaActivity.Tags.SubBatchIndex, subBatchIndex);
                subSpan?.SetTag(AristaActivity.Tags.SubBatchTotal, subBatchTotal);

                var slice = new List<ChunkDraft>(end - start);
                for (var k = start; k < end; k++)
                {
                    slice.Add(chunkSet.Leaves[k]);
                }

                var texts = slice.Select(d => d.Content).ToList();
                var vectors = await embedder.EmbedAsync(texts, isQuery: false, ct).ConfigureAwait(false);

                var chunks = new List<AristaChunk>(slice.Count);
                for (var j = 0; j < slice.Count; j++)
                {
                    var d = slice[j];
                    long? parentChunkId = d.ParentIndex is int pi
                        && pi >= 0 && pi < parentIds.Count
                        ? parentIds[pi]
                        : null;
                    chunks.Add(new AristaChunk
                    {
                        DocumentId = entry.Id,
                        ChunkIndex = parentRows.Count + start + j,
                        Content = d.Content,
                        RawContent = d.RawContent,
                        SectionTitle = d.SectionTitle,
                        SectionLevel = d.SectionLevel,
                        PageStart = d.PageStart,
                        PageEnd = d.PageEnd,
                        TokenCount = d.TokenCount,
                        ChunkKind = ChunkKind.Leaf,
                        ParentChunkId = parentChunkId,
                        Embedding = vectors[j],
                    });
                }

                inserted += await chunkRepo.BulkInsertAsync(chunks, ct).ConfigureAwait(false);

                if (leafCount > subBatchSize)
                {
                    progress.Log(
                        $"  {entry.Slug}: sub-batch {inserted}/{totalDrafts} chunks");
                }

                subBatchIndex++;
            }

            return (inserted, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, ex.Message);
        }
    }
}
