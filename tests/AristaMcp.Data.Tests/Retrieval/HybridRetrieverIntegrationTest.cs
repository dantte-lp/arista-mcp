using AristaMcp.Cli.Ingest;
using AristaMcp.Core.Chunking;
using AristaMcp.Core.Retrieval;
using AristaMcp.Data.Repositories;
using AristaMcp.Data.Tests.Fixtures;
using AristaMcp.Data.Tests.Ingest;
using AristaMcp.Server.Retrieval;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Data.Tests.Retrieval;

[Collection("Pgvector")]
public class HybridRetrieverIntegrationTest(PgvectorFixture fx)
{
    [Fact]
    public async Task SearchReturnsExpectedChunk_FromSeededCorpus()
    {
        await fx.ResetAsync();

        using var builder = new FakeCatalogBuilder();
        builder.AddDoc("evpn-doc", "evpn", "Arista EVPN Deployment Guide", "sha-evpn",
            """
            # BGP EVPN overlay

            EVPN is the de-facto Ethernet VPN control plane for VXLAN overlays in Arista EOS.
            Leafs advertise type-2 MAC/IP routes and type-5 IP prefix routes to spines via BGP.

            # Sample MLAG topology

            MLAG pairs two leaves for active-active LACP bundles toward servers.
            """);
        builder.AddDoc("ospf-doc", "ospf", "OSPF Area Design", "sha-ospf",
            """
            # OSPF single area

            OSPF is a link-state IGP. Single-area designs suit small campus networks.
            """);
        builder.AddDoc("cake-doc", "cake", "Chocolate Cake Recipe", "sha-cake",
            """
            # How to bake

            Mix flour sugar eggs. Bake at 180 degrees Celsius for thirty minutes.
            """);
        var catalogPath = builder.Build();

        await using var ctx = fx.CreateContext();
        var chunker = new SectionAwareChunker(new ChunkingOptions
        {
            TargetTokens = 80,
            MaxTokens = 160,
            OverlapTokens = 10,
            MinTokens = 5,
        });
        using var embedder = new DeterministicMockEmbedder();
        var service = new IngestService(
            chunker,
            embedder,
            new DocumentRepository(ctx, TimeProvider.System),
            new ChunkRepository(fx.DataSource, ctx),
            new IngestRunRepository(ctx, TimeProvider.System));
        var ingest = await service.IngestAsync(
            new IngestOptions { CatalogPath = catalogPath, Force = true },
            NullIngestProgress.Instance,
            CancellationToken.None);
        ingest.Status.Should().Be("success");

        using var reranker = new NoopReranker();
        var retriever = new HybridRetriever(embedder, reranker, fx.DataSource);

        var response = await retriever.SearchAsync(
            "EVPN overlay",
            new RetrievalOptions { Limit = 5, CandidatePoolSize = 20, RerankTopN = 10 },
            CancellationToken.None);

        response.Results.Should().NotBeEmpty();
        response.Results[0].DocumentId.Should().Be("evpn-doc",
            "dense + BM25 both favour the EVPN chunk; the cake recipe must rank lower");
        response.Diagnostics.SparseHits.Should().BeGreaterThan(0, "BM25 must hit the EVPN/overlay tokens");
        response.Diagnostics.DenseHits.Should().BeGreaterThan(0, "dense side must return candidates");
        response.Diagnostics.AfterRerank.Should().BeLessThanOrEqualTo(5);

        // Cake doc must not be top-ranked.
        response.Results[0].DocumentId.Should().NotBe("cake-doc");
    }
}
