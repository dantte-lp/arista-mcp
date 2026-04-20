namespace AristaMcp.Core.Retrieval;

public interface IReranker : IDisposable
{
    Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct);
}
