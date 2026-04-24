using AristaMcp.Core.Retrieval;
using AristaMcp.Embedding;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Embedding.Tests;

public class XlmRobertaOnnxRerankerTests
{
    private static (string Model, string Spm)? FindBgeAssets()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var sub in new[] { "reranker-bge", "reranker" })
            {
                var root = Path.Combine(dir.FullName, "models", sub);
                var model = Path.Combine(root, "model.onnx");
                var spm = Path.Combine(root, "sentencepiece.bpe.model");
                if (File.Exists(model) && File.Exists(spm))
                {
                    return (model, spm);
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    [SkippableFact]
    public async Task RelevantCandidateOutscoresIrrelevant()
    {
        var assets = FindBgeAssets();
        Skip.If(assets is null, "bge-reranker-base model.onnx + sentencepiece.bpe.model not present");

        using var reranker = new XlmRobertaOnnxReranker(new RerankerOptions
        {
            ModelPath = assets.Value.Model,
            VocabPath = assets.Value.Spm,
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
