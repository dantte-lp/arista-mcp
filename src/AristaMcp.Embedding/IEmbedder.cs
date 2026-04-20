namespace AristaMcp.Embedding;

public interface IEmbedder : IDisposable
{
    int Dimension { get; }

    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct);
}
