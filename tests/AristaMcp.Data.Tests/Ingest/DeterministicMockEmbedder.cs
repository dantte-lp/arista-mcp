using AristaMcp.Embedding;

namespace AristaMcp.Data.Tests.Ingest;

// Deterministic unit-vector embedder: hashes the text to pick a direction, produces a
// normalized 768-dim vector. Lets the ingest pipeline run without pulling the ONNX model
// (which would push test time from seconds to tens of seconds per run).
public sealed class DeterministicMockEmbedder : IEmbedder
{
    public int Dimension => 768;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var result = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            result[i] = VectorFor(texts[i]);
        }

        return Task.FromResult(result);
    }

    private static float[] VectorFor(string text)
    {
        var seed = 0;
        foreach (var c in text)
        {
            seed = (seed * 31) + c;
        }

        var rng = new Random(seed);
        var v = new float[768];
        double sq = 0;
        for (var d = 0; d < 768; d++)
        {
            v[d] = (rng.NextSingle() * 2f) - 1f;
            sq += v[d] * v[d];
        }

        var norm = (float)Math.Sqrt(sq);
        for (var d = 0; d < 768; d++)
        {
            v[d] /= norm;
        }

        return v;
    }

    public void Dispose() { }
}
