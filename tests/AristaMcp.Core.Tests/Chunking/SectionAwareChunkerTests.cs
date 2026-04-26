using AristaMcp.Core.Chunking;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Core.Tests.Chunking;

public class SectionAwareChunkerTests
{
    private static SectionAwareChunker Make(ChunkingOptions? opts = null) =>
        new(opts ?? new ChunkingOptions());

    [Fact]
    public void ShortSection_YieldsSingleChunk_WithTitlePrefix()
    {
        var chunker = Make();
        var sections = new List<Section>
        {
            new()
            {
                Title = "Introduction",
                Level = 1,
                Content = "MLAG pairs two switches into a single logical peer for downstream LACP bundles.",
            },
        };

        var result = chunker.Chunk("EOS User Manual", sections);

        result.Leaves.Should().HaveCount(1);
        result.Leaves[0].Content.Should().StartWith("EOS User Manual > Introduction\n\n");
        result.Leaves[0].RawContent.Should().NotStartWith("EOS User Manual");
        result.Leaves[0].SectionLevel.Should().Be((short)1);
        result.Parents.Should().HaveCount(1);
        result.Leaves[0].ParentIndex.Should().Be(0);
    }

    [Fact]
    public void EmptySectionIsSkipped()
    {
        var chunker = Make();
        var sections = new List<Section>
        {
            new() { Title = "Empty", Level = 1, Content = "   " },
            new() { Title = "Real", Level = 1, Content = "One short body." },
        };

        var result = chunker.Chunk("doc", sections);

        result.Leaves.Should().ContainSingle()
            .Which.SectionTitle.Should().Be("Real");
        result.Parents.Should().ContainSingle()
            .Which.SectionTitle.Should().Be("Real");
    }

    [Fact]
    public void OversizedSection_SplitsWithOverlap()
    {
        var opts = new ChunkingOptions
        {
            TargetTokens = 20,
            MaxTokens = 30,
            OverlapTokens = 4,
            MinTokens = 1,
        };
        var chunker = Make(opts);

        var body = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"w{i}"));
        var sections = new List<Section>
        {
            new() { Title = "Big", Level = 1, Content = body },
        };

        var result = chunker.Chunk("doc", sections);
        var chunks = result.Leaves;

        chunks.Should().HaveCountGreaterThan(5,
            "a 300-word section at TargetTokens=20 must split into several chunks");
        chunks.Should().AllSatisfy(c => c.TokenCount.Should().BeLessThanOrEqualTo(opts.MaxTokens));

        // Overlap check: last word of chunk N must reappear somewhere near the start of chunk N+1.
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            var prevTail = chunks[i].RawContent.Split(' ').TakeLast(2).First();
            var nextFirstFewWords = chunks[i + 1].RawContent.Split(' ').Take(10);
            nextFirstFewWords.Should().Contain(prevTail,
                $"chunk {i + 1} must share tail of chunk {i} (overlap window)");
        }
    }

    [Fact]
    public void TinyFollowup_MergedIntoPrevious_SameSection()
    {
        var opts = new ChunkingOptions
        {
            TargetTokens = 10,
            MaxTokens = 15,
            OverlapTokens = 0,
            MinTokens = 8,
        };
        var chunker = Make(opts);

        // 30 words = 3 chunks of ~10 words each at these settings; the trailing chunk
        // is likely < MinTokens, should merge into its predecessor.
        var body = string.Join(' ', Enumerable.Range(0, 22).Select(i => $"w{i}"));
        var sections = new List<Section>
        {
            new() { Title = "S", Level = 1, Content = body },
        };

        var chunks = chunker.Chunk("doc", sections).Leaves;
        chunks.Should().NotBeEmpty();
        chunks.Should().AllSatisfy(c => c.TokenCount.Should().BeGreaterThanOrEqualTo(opts.MinTokens),
            "tiny tail chunks should have been merged into the previous chunk");
    }

    [Fact]
    public void ChunkContent_UsesDocumentTitleAndSectionTitle_Prefix()
    {
        var chunker = Make();
        var sections = new List<Section>
        {
            new() { Title = "BGP EVPN", Level = 2, Content = "Overlay traffic uses VXLAN tunnels." },
        };

        var chunks = chunker.Chunk("Arista EOS Config Guide", sections).Leaves;

        chunks[0].Content.Should().Be(
            "Arista EOS Config Guide > BGP EVPN\n\nOverlay traffic uses VXLAN tunnels.");
    }
}
