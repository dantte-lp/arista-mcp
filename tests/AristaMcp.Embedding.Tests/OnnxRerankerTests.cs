using AristaMcp.Core.Retrieval;
using AristaMcp.Embedding;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Embedding.Tests;

public class OnnxRerankerTests
{
    private static string? FindRerankerDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models", "reranker", "model.onnx");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "models", "reranker");
            }

            dir = dir.Parent;
        }

        return null;
    }

    [SkippableFact]
    public async Task RelevantCandidateRanksAboveIrrelevant()
    {
        var dir = FindRerankerDir();
        Skip.If(dir is null, "models/reranker/model.onnx not present; run scripts/fetch-models.ps1");

        using var reranker = new OnnxReranker(new RerankerOptions
        {
            ModelPath = Path.Combine(dir!, "model.onnx"),
            VocabPath = Path.Combine(dir!, "vocab.txt"),
        });

        var candidates = new[]
        {
            new RerankCandidate(1L, "How to bake a chocolate cake at 180 degrees for thirty minutes."),
            new RerankCandidate(2L, "Configure MLAG peer-link on Arista 7050X3 switches for EVPN overlay."),
            new RerankCandidate(3L, "Static routing is a simple hub-and-spoke configuration."),
        };

        var results = await reranker.RerankAsync(
            "EVPN overlay configuration on Arista",
            candidates,
            CancellationToken.None);

        results.Should().HaveCount(3);

        var byId = results.ToDictionary(r => r.ChunkId, r => r.Score);
        byId[2L].Should().BeGreaterThan(byId[1L],
            "MLAG+EVPN chunk must outscore the cake recipe");
        byId[2L].Should().BeGreaterThan(byId[3L],
            "MLAG+EVPN chunk must outscore generic hub-and-spoke text");
    }
}
