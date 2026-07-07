using AristaMcp.Core.Chunking;
using AristaMcp.Core.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace AristaMcp.Data.Repositories;

public sealed class ChunkRepository(NpgsqlDataSource dataSource, AristaDbContext db) : IChunkRepository
{
    public async Task<int> BulkInsertAsync(IReadOnlyList<AristaChunk> chunks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            return 0;
        }

        // When the caller has an ambient EF transaction open (per-document ingest
        // wraps doc-upsert + delete + parent/leaf COPYs in one unit), the COPY must
        // run on that same connection so it commits or rolls back atomically with
        // the rest of the document. Npgsql binds COPY to the connection's active
        // transaction automatically. Otherwise (standalone bulk insert), open a
        // dedicated connection from the data source.
        var ambient = db.Database.CurrentTransaction;
        NpgsqlConnection conn;
        NpgsqlConnection? owned = null;
        if (ambient is not null)
        {
            conn = (NpgsqlConnection)db.Database.GetDbConnection();
        }
        else
        {
            owned = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            conn = owned;
        }

        try
        {
            return await CopyRowsAsync(conn, chunks, ct).ConfigureAwait(false);
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> CopyRowsAsync(
        NpgsqlConnection conn, IReadOnlyList<AristaChunk> chunks, CancellationToken ct)
    {
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY chunks (document_id, chunk_index, content, raw_content, "
            + "section_title, section_level, page_start, page_end, token_count, "
            + "embedding, embedding_model, chunk_kind, parent_chunk_id) FROM STDIN BINARY",
            ct).ConfigureAwait(false);

        foreach (var c in chunks)
        {
            await writer.StartRowAsync(ct).ConfigureAwait(false);
            await writer.WriteAsync(c.DocumentId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(c.ChunkIndex, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            await writer.WriteAsync(c.Content, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(c.RawContent, NpgsqlDbType.Text, ct).ConfigureAwait(false);

            if (c.SectionTitle is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.SectionTitle, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            }

            if (c.SectionLevel is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.SectionLevel.Value, NpgsqlDbType.Smallint, ct).ConfigureAwait(false);
            }

            if (c.PageStart is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.PageStart.Value, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            }

            if (c.PageEnd is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.PageEnd.Value, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            }

            await writer.WriteAsync(c.TokenCount, NpgsqlDbType.Integer, ct).ConfigureAwait(false);

            // Embedding is null on parent rows (chunk_kind='parent') because
            // parents are never embedded; the HNSW index simply does not see
            // those rows. Leaves always carry an embedding vector.
            if (c.Embedding is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                Half[] halfArr = [.. c.Embedding.Select(static f => (Half)f)];
                await writer.WriteAsync(new HalfVector(halfArr), ct).ConfigureAwait(false);
            }

            if (c.EmbeddingModel is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.EmbeddingModel, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            }

            await writer.WriteAsync(
                c.ChunkKind == ChunkKind.Parent ? "parent" : "leaf",
                NpgsqlDbType.Text, ct).ConfigureAwait(false);

            if (c.ParentChunkId is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.ParentChunkId.Value, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
            }
        }

        await writer.CompleteAsync(ct).ConfigureAwait(false);
        return chunks.Count;
    }

    // Sprint 15: returns parent ids (ordered by chunk_index ascending) for a
    // single document, so the caller can patch leaf parent_chunk_id values
    // before the second-pass leaf insert. Used inside the per-doc ingest
    // transaction immediately after BulkInsertAsync of parents.
    public async Task<IReadOnlyList<long>> SelectParentIdsAsync(
        string documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        var ids = await db.Chunks
            .Where(c => c.DocumentId == documentId && c.ChunkKind == "parent")
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        return ids;
    }

    public Task<int> DeleteByDocumentAsync(string documentId, CancellationToken ct) =>
        db.Chunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync(ct);

    public Task<int> CountAsync(CancellationToken ct) => db.Chunks.CountAsync(ct);
}
