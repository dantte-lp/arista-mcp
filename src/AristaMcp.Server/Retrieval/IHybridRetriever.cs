using AristaMcp.Core.Models;

namespace AristaMcp.Server.Retrieval;

public interface IHybridRetriever
{
    Task<SearchResponse> SearchAsync(string query, RetrievalOptions options, CancellationToken ct);
}
