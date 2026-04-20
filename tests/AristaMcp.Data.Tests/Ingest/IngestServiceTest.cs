using AristaMcp.Cli.Ingest;
using AristaMcp.Core.Chunking;
using AristaMcp.Data.Repositories;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Data.Tests.Ingest;

[Collection("Pgvector")]
public class IngestServiceTest(PgvectorFixture fx)
{
    private static IngestService MakeService(PgvectorFixture pg, Data.AristaDbContext ctx)
    {
        var chunker = new SectionAwareChunker(new ChunkingOptions
        {
            TargetTokens = 80,
            MaxTokens = 160,
            OverlapTokens = 10,
            MinTokens = 5,
        });
        var embedder = new DeterministicMockEmbedder();
        var docRepo = new DocumentRepository(ctx);
        var chunkRepo = new ChunkRepository(pg.DataSource, ctx);
        var runRepo = new IngestRunRepository(ctx);
        return new IngestService(chunker, embedder, docRepo, chunkRepo, runRepo);
    }

    [Fact]
    public async Task IngestsFiveDocs_ProducesAtLeast30Chunks_AllWithBm25()
    {
        await fx.ResetAsync();

        using var builder = new FakeCatalogBuilder();
        for (var i = 0; i < 5; i++)
        {
            builder.AddDoc(
                id: $"doc-{i}",
                slug: $"fake-{i}",
                title: $"Fake Doc {i}",
                pdfSha256: $"sha{i:D2}",
                markdown: BuildMarkdown(i));
        }

        var catalogPath = builder.Build();

        await using var ctx = fx.CreateContext();
        var service = MakeService(fx, ctx);

        var result = await service.IngestAsync(
            new IngestOptions { CatalogPath = catalogPath, Force = true },
            NullIngestProgress.Instance,
            CancellationToken.None);

        result.Status.Should().Be("success");
        result.DocsTotal.Should().Be(5);
        result.DocsUpserted.Should().Be(5);
        result.ChunksUpserted.Should().BeGreaterThanOrEqualTo(30);

        // Every chunk must have its bm25v populated by the trigger.
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE bm25v IS NULL;";
        var nullCount = (long?)await cmd.ExecuteScalarAsync() ?? -1;
        nullCount.Should().Be(0, "create_custom_model_tokenizer_and_trigger must populate bm25v on every insert");
    }

    [Fact]
    public async Task Reingest_WithSameCatalogSha_Skips()
    {
        await fx.ResetAsync();

        using var builder = new FakeCatalogBuilder();
        builder.AddDoc("d1", "s1", "Doc 1", "sha1", BuildMarkdown(1));
        var catalogPath = builder.Build();

        await using var ctx1 = fx.CreateContext();
        var svc1 = MakeService(fx, ctx1);
        var first = await svc1.IngestAsync(
            new IngestOptions { CatalogPath = catalogPath },
            NullIngestProgress.Instance,
            CancellationToken.None);
        first.Status.Should().Be("success");

        await using var ctx2 = fx.CreateContext();
        var svc2 = MakeService(fx, ctx2);
        var second = await svc2.IngestAsync(
            new IngestOptions { CatalogPath = catalogPath },
            NullIngestProgress.Instance,
            CancellationToken.None);

        second.Status.Should().Be("skipped");
        second.DocsUpserted.Should().Be(0);
    }

    [Fact]
    public async Task Reingest_WithChangedPdfSha_RefreshesOnlyThatDoc()
    {
        await fx.ResetAsync();

        using var initial = new FakeCatalogBuilder();
        initial.AddDoc("d1", "s1", "Doc 1", "sha-v1", BuildMarkdown(1));
        initial.AddDoc("d2", "s2", "Doc 2", "sha-v1", BuildMarkdown(2));
        var initialCatalog = initial.Build();

        await using (var ctx1 = fx.CreateContext())
        {
            var svc1 = MakeService(fx, ctx1);
            var r = await svc1.IngestAsync(
                new IngestOptions { CatalogPath = initialCatalog },
                NullIngestProgress.Instance,
                CancellationToken.None);
            r.DocsUpserted.Should().Be(2);
        }

        // Rebuild catalog with d1's sha bumped; catalog SHA changes, so the catalog-level
        // early-exit doesn't fire. d2 keeps its sha and must be skipped.
        using var updated = new FakeCatalogBuilder();
        updated.AddDoc("d1", "s1", "Doc 1", "sha-v2", BuildMarkdown(1) + "\n\n# Extra section\n\nmore body");
        updated.AddDoc("d2", "s2", "Doc 2", "sha-v1", BuildMarkdown(2));
        var updatedCatalog = updated.Build();

        await using var ctx2 = fx.CreateContext();
        var svc2 = MakeService(fx, ctx2);
        var result = await svc2.IngestAsync(
            new IngestOptions { CatalogPath = updatedCatalog },
            NullIngestProgress.Instance,
            CancellationToken.None);

        result.Status.Should().Be("success");
        result.DocsUpserted.Should().Be(1, "only d1's PDF sha changed");
        result.DocsSkipped.Should().Be(1, "d2's sha is unchanged");
    }

    private static string BuildMarkdown(int seed)
    {
        var words = string.Join(' ', Enumerable.Range(0, 200).Select(i => $"w{seed}_{i}"));
        return $"""
            # Section A

            {words}

            ## Subsection A1

            {words}

            # Section B

            {words}
            """;
    }
}
