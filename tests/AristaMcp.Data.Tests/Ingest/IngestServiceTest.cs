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
        var docRepo = new DocumentRepository(ctx, TimeProvider.System);
        var chunkRepo = new ChunkRepository(pg.DataSource, ctx);
        var runRepo = new IngestRunRepository(ctx, TimeProvider.System);
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

    [Fact]
    public async Task PlaceholderDoc_StartsWithFakeConversionHeading_IsSkipped()
    {
        // Sprint 8-post-mortem canary: upstream arista-docs used to stamp
        // placeholder MD as convert_mode="accurate" and ingest would happily
        // chunk "Fake conversion of X.pdf\n\n<N> bytes." into the BM25 index,
        // where it matched every query that shared a subword. The defensive
        // filter in IngestService.IngestDocumentAsync now catches this at the
        // title level. This test locks that in.
        await fx.ResetAsync();

        using var builder = new FakeCatalogBuilder();
        // Exact shape FakeConverter wrote pre-fix.
        builder.AddDoc(
            "placeholder", "fake-doc", "Fake Doc", "sha-placeholder",
            "# Fake conversion of fake-doc.pdf\n\n72839 bytes.\n");
        builder.AddDoc(
            "real", "real-doc", "Real Doc", "sha-real", BuildMarkdown(1));
        var catalogPath = builder.Build();

        await using var ctx = fx.CreateContext();
        var service = MakeService(fx, ctx);

        var result = await service.IngestAsync(
            new IngestOptions { CatalogPath = catalogPath, Force = true },
            NullIngestProgress.Instance,
            CancellationToken.None);

        // Placeholder doesn't upsert; real doc does.
        result.DocsUpserted.Should().Be(1, "real-doc produced chunks, placeholder was skipped");

        // The literal BM25-poisoning phrase must not appear in any chunk row.
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE content LIKE 'Fake conversion of%'";
        var leaked = (long?)await cmd.ExecuteScalarAsync() ?? -1;
        leaked.Should().Be(0, "defensive filter must drop the FakeConverter heading before BulkInsert");
    }

    [Fact]
    public async Task PlaceholderDoc_BelowBodyCharThreshold_IsSkipped()
    {
        // Signature-agnostic fallback: even if a future arista-docs placeholder
        // format avoids the "Fake conversion of" prefix, a sum of section-body
        // text below 40 chars is still a strong placeholder signal. This test
        // pins the body-size gate.
        await fx.ResetAsync();

        using var builder = new FakeCatalogBuilder();
        // 30 chars of body — below the 40-char gate. Title DOESN'T match the
        // fake-prefix filter, so this exercises the second gate.
        builder.AddDoc(
            "tiny", "tiny-doc", "Tiny Doc", "sha-tiny",
            "# Something Unrelated\n\ntiny thirty chars body\n");
        var catalogPath = builder.Build();

        await using var ctx = fx.CreateContext();
        var service = MakeService(fx, ctx);

        var result = await service.IngestAsync(
            new IngestOptions { CatalogPath = catalogPath, Force = true },
            NullIngestProgress.Instance,
            CancellationToken.None);

        result.DocsUpserted.Should().Be(0, "body < 40 chars — placeholder gate trips");
        result.ChunksUpserted.Should().Be(0);
    }

    [Fact]
    public async Task SubBatching_SplitsHugeDocIntoMultipleInserts_FinalChunkCountStable()
    {
        // Sprint 8.2 canary: when a doc produces more chunks than
        // ChunkSubBatchSize, IngestService commits them in multiple
        // BulkInserts. The final on-disk row count, chunk index monotonicity,
        // and bm25v trigger coverage must match the all-at-once baseline.
        await fx.ResetAsync();

        using var builder = new FakeCatalogBuilder();
        builder.AddDoc("big", "big-doc", "Big Doc", "sha-big", BuildManySectionsMarkdown(sections: 60));
        var catalogPath = builder.Build();

        await using var ctx = fx.CreateContext();
        var service = MakeService(fx, ctx);

        // Tiny sub-batch forces the loop to trip. 60 sections × chunker
        // produces ~60 chunks; with SubBatchSize = 10 that's 6 sub-batches.
        var result = await service.IngestAsync(
            new IngestOptions
            {
                CatalogPath = catalogPath,
                Force = true,
                ChunkSubBatchSize = 10,
            },
            NullIngestProgress.Instance,
            CancellationToken.None);

        result.Status.Should().Be("success");
        result.DocsUpserted.Should().Be(1);
        result.ChunksUpserted.Should().BeGreaterThanOrEqualTo(60);

        // Chunk indices must be dense 0..N-1, not gapped or reset per sub-batch.
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = """
            SELECT MIN(chunk_index), MAX(chunk_index), COUNT(*) FROM chunks WHERE document_id = 'big';
            """;
        await using var r = await idxCmd.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue();
        var minIdx = r.GetInt32(0);
        var maxIdx = r.GetInt32(1);
        var count = r.GetInt64(2);
        minIdx.Should().Be(0);
        (maxIdx - minIdx + 1).Should().Be((int)count, "indices must be contiguous across sub-batches");
    }

    private static string BuildManySectionsMarkdown(int sections)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < sections; i++)
        {
            sb.Append("# Section ").Append(i).Append("\n\n");
            for (var w = 0; w < 80; w++)
            {
                sb.Append("tok").Append(i).Append('_').Append(w).Append(' ');
            }
            sb.Append("\n\n");
        }
        return sb.ToString();
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
