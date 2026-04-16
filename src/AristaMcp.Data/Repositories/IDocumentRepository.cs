using AristaMcp.Core.Models;

namespace AristaMcp.Data.Repositories;

public interface IDocumentRepository
{
    Task UpsertAsync(AristaDocument doc, CancellationToken ct);
    Task<AristaDocument?> GetByIdAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<string>> GetAllIdsAsync(CancellationToken ct);
    Task<string?> GetPdfSha256Async(string id, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}
