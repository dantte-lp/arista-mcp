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

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY chunks (document_id, chunk_index, content, raw_content, "
            + "section_title, section_level, page_start, page_end, token_count, "
            + "embedding, embedding_model) FROM STDIN BINARY",
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

            Half[] halfArr = [.. c.Embedding.Select(static f => (Half)f)];
            await writer.WriteAsync(new HalfVector(halfArr), ct).ConfigureAwait(false);
            await writer.WriteAsync(c.EmbeddingModel, NpgsqlDbType.Text, ct).ConfigureAwait(false);
        }

        await writer.CompleteAsync(ct).ConfigureAwait(false);
        return chunks.Count;
    }

    public Task<int> DeleteByDocumentAsync(string documentId, CancellationToken ct) =>
        db.Chunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync(ct);

    public Task<int> CountAsync(CancellationToken ct) => db.Chunks.CountAsync(ct);
}
