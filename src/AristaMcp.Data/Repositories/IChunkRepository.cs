using AristaMcp.Core.Models;

namespace AristaMcp.Data.Repositories;

public interface IChunkRepository
{
    Task<int> BulkInsertAsync(IReadOnlyList<AristaChunk> chunks, CancellationToken ct);
    Task<int> DeleteByDocumentAsync(string documentId, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
