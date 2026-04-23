using AristaMcp.Cli.Benchmarks;
using AristaMcp.Cli.Curation;
using AristaMcp.Core.Models;
using AristaMcp.Server.Retrieval;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace AristaMcp.Data.Tests.Curation;

// Pure-logic tests for TripleCurator. Stubs IHybridRetriever via NSubstitute
// so no DB or embedder is involved. Asserts on positive selection, hard
// negative filtering (must differ in doc AND product), and JSONL shape.
public class TripleCuratorTest
{
    [Fact]
    public async Task Curate_PicksTopRankedMatchAsPositive_AndFourCrossProductNegatives()
    {
        var query = new BenchmarkQuery
        {
            Query = "MLAG peer-link setup",
            ExpectAny = ["MLAG"],
        };
        var results = BuildResults([
            // Positive candidate at rank 0 (matches ExpectAny "MLAG")
            Chunk(id: 100, docId: "eos-mlag", slug: "EOS-MLAG-Guide", title: "MLAG Configuration Guide", product: "eos"),
            // Rank 1: same product as positive — filtered out
            Chunk(id: 101, docId: "eos-evpn", slug: "EOS-EVPN", title: "EVPN Overlay", product: "eos"),
            // Rank 2: cross-product = hard negative
            Chunk(id: 102, docId: "cvp-guide", slug: "CVP-Ops", title: "CloudVision Operations", product: "cvp"),
            // Rank 3: also cross-product
            Chunk(id: 103, docId: "dmf-arch", slug: "DMF-Arch", title: "DMF Architecture", product: "dmf"),
            // Rank 4: cross-product
            Chunk(id: 104, docId: "hw-7050", slug: "7050X3-DS", title: "7050X3 Datasheet", product: "hardware"),
            // Rank 5: cross-product
            Chunk(id: 105, docId: "cve-cloud", slug: "CloudEOS-VPC", title: "CloudEOS VPC", product: "cloudeos"),
        ]);
        var retriever = StubRetriever(results);

        var (triples, stats) = await TripleCurator.CurateAsync(
            [query], retriever, negativesPerQuery: 4, ct: CancellationToken.None);

        stats.QueriesTotal.Should().Be(1);
        stats.QueriesWithPositive.Should().Be(1);
        stats.TriplesEmitted.Should().Be(1);

        var t = triples.Single();
        t.Query.Should().Be(query.Query);
        t.Positive.ChunkId.Should().Be(100);
        t.Positive.Product.Should().Be("eos");

        t.Negatives.Should().HaveCount(4);
        t.Negatives.Select(n => n.ChunkId).Should().BeEquivalentTo([102L, 103L, 104L, 105L]);
        t.Negatives.Should().NotContain(n => n.Product == "eos", "same-product chunks are filtered");
        t.Negatives.Should().NotContain(n => n.ChunkId == 100, "positive excluded");
        t.Negatives.Should().NotContain(n => n.DocumentId == "eos-mlag", "sibling chunks excluded");
    }

    [Fact]
    public async Task Curate_NoMatchingResult_EmitsNoTriple()
    {
        var query = new BenchmarkQuery
        {
            Query = "VXLAN gateway",
            ExpectAny = ["VXLAN"],
        };
        // All results are off-product with no VXLAN substring match
        var results = BuildResults([
            Chunk(id: 1, docId: "cvp-a", slug: "CVP-A", title: "CVP alpha", product: "cvp"),
            Chunk(id: 2, docId: "cvp-b", slug: "CVP-B", title: "CVP beta", product: "cvp"),
        ]);
        var retriever = StubRetriever(results);

        var (triples, stats) = await TripleCurator.CurateAsync(
            [query], retriever, negativesPerQuery: 1, ct: CancellationToken.None);

        triples.Should().BeEmpty();
        stats.QueriesSkippedNoPositive.Should().Be(1);
    }

    [Fact]
    public async Task Curate_InsufficientNegatives_DropsQuery()
    {
        var query = new BenchmarkQuery
        {
            Query = "spanning tree",
            ExpectAny = ["STP", "spanning"],
        };
        // Only one cross-product negative; we ask for 2 → dropped
        var results = BuildResults([
            Chunk(id: 1, docId: "eos-stp", slug: "spanning-tree", title: "STP", product: "eos"),
            Chunk(id: 2, docId: "eos-misc", slug: "misc", title: "Other EOS", product: "eos"),
            Chunk(id: 3, docId: "cvp-misc", slug: "cvp-misc", title: "CVP Misc", product: "cvp"),
        ]);
        var retriever = StubRetriever(results);

        var (triples, stats) = await TripleCurator.CurateAsync(
            [query], retriever, negativesPerQuery: 2, ct: CancellationToken.None);

        triples.Should().BeEmpty();
        stats.QueriesWithPositive.Should().Be(1);
        stats.QueriesSkippedInsufficientNegatives.Should().Be(1);
    }

    [Fact]
    public async Task WriteJsonl_EmitsOneLinePerTriple_ValidShape()
    {
        var query = new BenchmarkQuery { Query = "q", ExpectAny = ["EOS"] };
        var results = BuildResults([
            Chunk(id: 1, docId: "d1", slug: "EOS-x", title: "EOS thing", product: "eos"),
            Chunk(id: 2, docId: "d2", slug: "cvp-y", title: "cvp thing", product: "cvp"),
        ]);
        var retriever = StubRetriever(results);

        var (triples, _) = await TripleCurator.CurateAsync(
            [query], retriever, negativesPerQuery: 1, ct: CancellationToken.None);

        var tmp = Path.Combine(Path.GetTempPath(), $"triples-{Guid.NewGuid():N}.jsonl");
        try
        {
            var written = await TripleCurator.WriteJsonlAsync(triples, tmp, CancellationToken.None);
            written.Should().Be(1);

            var lines = await File.ReadAllLinesAsync(tmp);
            lines.Should().HaveCount(1);
            var payload = System.Text.Json.JsonDocument.Parse(lines[0]).RootElement;
            payload.GetProperty("query").GetString().Should().Be("q");
            payload.GetProperty("positive").GetProperty("chunk_id").GetInt64().Should().Be(1);
            payload.GetProperty("negatives").GetArrayLength().Should().Be(1);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static IHybridRetriever StubRetriever(IReadOnlyList<ChunkResult> results)
    {
        var stub = Substitute.For<IHybridRetriever>();
        stub.SearchAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SearchResponse(
                Results: results,
                Diagnostics: new SearchDiagnostics(
                    DenseHits: 0, SparseHits: 0, AfterRrf: 0, AfterRerank: 0,
                    EmbedMs: 0, DenseQueryMs: 0, SparseQueryMs: 0,
                    RrfMs: 0, RerankMs: 0, TotalMs: 0))));
        return stub;
    }

    private static IReadOnlyList<ChunkResult> BuildResults(IEnumerable<ChunkResult> items) => [.. items];

    private static ChunkResult Chunk(
        long id, string docId, string slug, string title, string product) =>
        new(
            ChunkId: id,
            DocumentId: docId,
            DocumentTitle: title,
            DocumentSlug: slug,
            Category: "manual",
            Product: product,
            Version: null,
            SectionTitle: null,
            SectionLevel: null,
            PageStart: null,
            PageEnd: null,
            RawContent: $"text of chunk {id}",
            Score: 0.5f,
            DenseSimilarity: null,
            Bm25Score: null,
            RrfScore: null,
            RerankScore: null);
}
