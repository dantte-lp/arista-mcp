using AristaMcp.Core.Retrieval;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Core.Tests.Retrieval;

public class NoopRerankerTests
{
    [Fact]
    public async Task PreservesInputOrder_WithDescendingScores()
    {
        using var reranker = new NoopReranker();
        var candidates = new[]
        {
            new RerankCandidate(10L, "first"),
            new RerankCandidate(20L, "second"),
            new RerankCandidate(30L, "third"),
        };

        var results = await reranker.RerankAsync("any query", candidates, CancellationToken.None);

        results.Should().HaveCount(3);
        results[0].ChunkId.Should().Be(10L);
        results[1].ChunkId.Should().Be(20L);
        results[2].ChunkId.Should().Be(30L);
        results[0].Score.Should().BeGreaterThan(results[1].Score);
        results[1].Score.Should().BeGreaterThan(results[2].Score);
    }

    [Fact]
    public async Task EmptyCandidates_ReturnsEmpty()
    {
        using var reranker = new NoopReranker();

        var results = await reranker.RerankAsync("q", [], CancellationToken.None);

        results.Should().BeEmpty();
    }
}
