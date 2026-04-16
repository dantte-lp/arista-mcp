using System.Diagnostics;
using AristaMcp.Core.Models;
using AristaMcp.Data.Entities;
using AristaMcp.Data.Repositories;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class ChunkRepositoryBulkInsertTest(PgvectorFixture fx)
{
    [Fact]
    public async Task BulkInsert1000Chunks_CompletesUnderThreeSeconds()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();
        ctx.Documents.Add(new DocumentEntity
        {
            Id = "bulk-doc",
            Url = "u",
            Category = "toi",
            Title = "T",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        });
        await ctx.SaveChangesAsync();

        var rng = new Random(7);
        var chunks = Enumerable.Range(0, 1000).Select(i =>
        {
            var vec = new float[768];
            for (var j = 0; j < 768; j++)
            {
                vec[j] = rng.NextSingle();
            }

            return new AristaChunk
            {
                DocumentId = "bulk-doc",
                ChunkIndex = i,
                Content = $"title > s\n\nbody {i}",
                RawContent = $"body {i}",
                TokenCount = 4,
                Embedding = vec,
            };
        }).ToList();

        var repo = new ChunkRepository(fx.DataSource, ctx);
        var sw = Stopwatch.StartNew();
        var inserted = await repo.BulkInsertAsync(chunks, CancellationToken.None);
        sw.Stop();

        inserted.Should().Be(1000);
        sw.ElapsedMilliseconds.Should().BeLessThan(3000);

        var count = await repo.CountAsync(CancellationToken.None);
        count.Should().BeGreaterThanOrEqualTo(1000);
    }

    [Fact]
    public async Task DeleteByDocument_RemovesAllChunks()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();
        ctx.Documents.Add(new DocumentEntity
        {
            Id = "del-doc",
            Url = "u",
            Category = "toi",
            Title = "T",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        });
        await ctx.SaveChangesAsync();

        var chunks = Enumerable.Range(0, 10).Select(i => new AristaChunk
        {
            DocumentId = "del-doc",
            ChunkIndex = i,
            Content = $"c{i}",
            RawContent = $"c{i}",
            TokenCount = 1,
            Embedding = [.. Enumerable.Repeat(0.1f, 768)],
        }).ToList();

        var repo = new ChunkRepository(fx.DataSource, ctx);
        await repo.BulkInsertAsync(chunks, CancellationToken.None);

        var deleted = await repo.DeleteByDocumentAsync("del-doc", CancellationToken.None);
        deleted.Should().Be(10);
    }
}
