using AristaMcp.Embedding;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Embedding.Tests;

// Byte-for-byte parity against the HuggingFace BAAI/bge-reranker-base
// tokenizer. Reference IDs were captured via transformers AutoTokenizer
// with use_fast=False in Python; if these drift the .NET fairseq-offset
// remap broke.
public class XlmRobertaRerankerTokenizerTests
{
    private static string? FindSpmPath()
    {
        // Walk up looking for models/reranker-bge/sentencepiece.bpe.model first
        // (staging location); fall back to models/reranker/sentencepiece.bpe.model
        // (post-cutover canonical location).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var sub in new[] { "reranker-bge", "reranker" })
            {
                var c = Path.Combine(dir.FullName, "models", sub, "sentencepiece.bpe.model");
                if (File.Exists(c))
                {
                    return c;
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    [SkippableFact]
    public void EncodeBare_MatchesHuggingFace()
    {
        var spm = FindSpmPath();
        Skip.If(spm is null, "sentencepiece.bpe.model not found under models/reranker-bge or models/reranker");

        var tok = new XlmRobertaRerankerTokenizer(spm!);

        tok.EncodeBare("Hello, world!").Should()
            .Equal(35378, 4, 8999, 38);

        tok.EncodeBare("Arista EOS BGP configuration").Should()
            .Equal(1172, 1035, 163795, 335, 32566, 180346);

        tok.EncodeBare("EVPN VXLAN MLAG").Should()
            .Equal(57794, 34440, 310, 1542, 29946, 276, 91912);
    }

    [Fact]
    public void SpecialTokenConstants_MatchHuggingFaceFairseqMap()
    {
        XlmRobertaRerankerTokenizer.BosTokenId.Should().Be(0);
        XlmRobertaRerankerTokenizer.PadTokenId.Should().Be(1);
        XlmRobertaRerankerTokenizer.EosTokenId.Should().Be(2);
        XlmRobertaRerankerTokenizer.UnkTokenId.Should().Be(3);
    }
}
