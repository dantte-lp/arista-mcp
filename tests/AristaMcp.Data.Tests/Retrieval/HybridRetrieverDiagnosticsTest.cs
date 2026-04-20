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
public class HybridRetrieverDiagnosticsTest(PgvectorFixture fx)
{
    [Fact]
    public async Task Bm25Score_OnDenseSparseCoHit_IsNegativeBm25_NotNegativeCosineDistance()
    {
        await fx.ResetAsync();

        // Seed a corpus where the "winning" chunk reliably scores on both sides:
        // the EVPN chunk has tokens matching the query AND embeds close to it.
        using var builder = new FakeCatalogBuilder();
        builder.AddDoc("evpn-doc", "evpn", "Arista EVPN Guide", "sha-evpn",
            """
            # BGP EVPN overlay

            EVPN is the de-facto Ethernet VPN control plane for VXLAN overlays.
            Leafs advertise type-2 MAC/IP and type-5 IP prefix routes via BGP.

            # Fabric design

            Common fabrics use spine-leaf topologies with EVPN overlay.
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
        var svc = new IngestService(
            chunker,
            embedder,
            new DocumentRepository(ctx, TimeProvider.System),
            new ChunkRepository(fx.DataSource, ctx),
            new IngestRunRepository(ctx, TimeProvider.System));
        var ingest = await svc.IngestAsync(
            new IngestOptions { CatalogPath = catalogPath, Force = true },
            NullIngestProgress.Instance,
            CancellationToken.None);
        ingest.Status.Should().Be("success");

        using var reranker = new NoopReranker();
        var retriever = new HybridRetriever(embedder, reranker, fx.DataSource);

        var resp = await retriever.SearchAsync(
            "EVPN overlay",
            new RetrievalOptions { Limit = 5, CandidatePoolSize = 20, RerankTopN = 10 },
            CancellationToken.None);

        resp.Results.Should().NotBeEmpty();
        var top = resp.Results[0];

        // Regression: the EVPN chunk lands on both dense and sparse sides, so both
        // score fields must be populated (not null) and the retriever must read each
        // distance from its own SQL branch — not fall back to the dense row's cosine.
        top.Bm25Score.Should().NotBeNull("EVPN/overlay tokens hit the BM25 index");
        top.DenseSimilarity.Should().NotBeNull("dense side returned the same chunk");

        // Bm25Score is sign-flipped to "higher = better" convention. A matching chunk
        // produces a BM25 value > 1 for this kind of corpus. Cosine similarity lives
        // in [-1, 1] — if Bm25Score were mistakenly derived from the dense distance,
        // it could not exceed 1.
        top.Bm25Score!.Value.Should().BeGreaterThan(1f,
            $"real BM25 score for a matching chunk must exceed 1; got {top.Bm25Score.Value:F3}");
    }

    [Fact]
    public async Task Diagnostics_DenseAndSparseQueryMs_ArePopulated()
    {
        await fx.ResetAsync();

        using var builder = new FakeCatalogBuilder();
        builder.AddDoc("t-doc", "t", "Tiny", "sha-t", "# A\n\nEVPN overlay body text.\n");
        var catalogPath = builder.Build();

        await using var ctx = fx.CreateContext();
        var chunker = new SectionAwareChunker(new ChunkingOptions
        {
            TargetTokens = 40,
            MaxTokens = 80,
            OverlapTokens = 5,
            MinTokens = 5,
        });
        using var embedder = new DeterministicMockEmbedder();
        var svc = new IngestService(
            chunker,
            embedder,
            new DocumentRepository(ctx, TimeProvider.System),
            new ChunkRepository(fx.DataSource, ctx),
            new IngestRunRepository(ctx, TimeProvider.System));
        await svc.IngestAsync(
            new IngestOptions { CatalogPath = catalogPath, Force = true },
            NullIngestProgress.Instance,
            CancellationToken.None);

        using var reranker = new NoopReranker();
        var retriever = new HybridRetriever(embedder, reranker, fx.DataSource);

        var resp = await retriever.SearchAsync(
            "EVPN overlay",
            new RetrievalOptions { Limit = 5, CandidatePoolSize = 10, RerankTopN = 5 },
            CancellationToken.None);

        resp.Diagnostics.DenseQueryMs.Should().BeGreaterThan(0.0,
            "dense SQL wall-clock must be measured, not hard-coded to 0");
        resp.Diagnostics.SparseQueryMs.Should().BeGreaterThan(0.0,
            "sparse SQL wall-clock must be measured, not hard-coded to 0");
        resp.Diagnostics.TotalMs.Should().BeGreaterThan(0.0);
    }
}
