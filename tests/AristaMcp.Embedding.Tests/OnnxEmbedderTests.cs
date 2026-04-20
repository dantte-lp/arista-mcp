using AristaMcp.Embedding;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Embedding.Tests;

public class OnnxEmbedderTests
{
    private static string? FindModelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models", "embedder", "model.onnx");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "models", "embedder");
            }

            dir = dir.Parent;
        }

        return null;
    }

    [SkippableFact]
    public async Task EmbedsA768DimensionalUnitVector()
    {
        var modelsDir = FindModelsDir();
        Skip.If(modelsDir is null, "models/embedder/model.onnx not present; run scripts/fetch-models.ps1");

        using var embedder = new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = Path.Combine(modelsDir!, "model.onnx"),
            VocabPath = Path.Combine(modelsDir!, "vocab.txt"),
        });

        var vecs = await embedder.EmbedAsync(["hello world"], isQuery: false, CancellationToken.None);

        vecs.Should().HaveCount(1);
        vecs[0].Length.Should().Be(768);

        double norm = 0;
        foreach (var v in vecs[0])
        {
            norm += v * v;
        }

        Math.Sqrt(norm).Should().BeApproximately(1.0, 1e-3);
    }

    [SkippableFact]
    public async Task RelatedTextsHaveHigherCosineSimilarityThanUnrelated()
    {
        var modelsDir = FindModelsDir();
        Skip.If(modelsDir is null, "models/embedder/model.onnx not present; run scripts/fetch-models.ps1");

        using var embedder = new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = Path.Combine(modelsDir!, "model.onnx"),
            VocabPath = Path.Combine(modelsDir!, "vocab.txt"),
        });

        var vecs = await embedder.EmbedAsync(
            [
                "Configure MLAG peer-link on Arista 7050X3 switches.",
                "MLAG peer link configuration for Arista data center switches.",
                "Bake a chocolate cake at 180 degrees for 30 minutes.",
            ],
            isQuery: false,
            CancellationToken.None);

        var simRelated = Cosine(vecs[0], vecs[1]);
        var simUnrelated = Cosine(vecs[0], vecs[2]);

        simRelated.Should().BeGreaterThan(simUnrelated,
            "two MLAG sentences should be more similar to each other than to a cake recipe");
        simRelated.Should().BeGreaterThan(0.6f, "semantically-similar sentences should cluster tightly");
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot; // inputs are already L2-normalized, so dot == cosine
    }
}
