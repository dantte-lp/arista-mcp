using AristaMcp.Data.Entities;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class HnswIndexSearchTest(PgvectorFixture fx)
{
    [Fact]
    public async Task NearestNeighboursReturnedInOrder()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();
        var rng = new Random(42);

        ctx.Documents.Add(new DocumentEntity
        {
            Id = "hnsw-doc",
            Url = "u",
            Category = "toi",
            Title = "T",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        });
        await ctx.SaveChangesAsync();

        const int chunkCount = 500;
        for (var i = 0; i < chunkCount; i++)
        {
            var v = new Half[768];
            for (var j = 0; j < 768; j++)
            {
                v[j] = (Half)((rng.NextSingle() * 2f) - 1f);
            }

            ctx.Chunks.Add(new ChunkEntity
            {
                DocumentId = "hnsw-doc",
                ChunkIndex = i,
                Content = $"chunk {i}",
                RawContent = $"chunk {i}",
                TokenCount = 1,
                Embedding = new HalfVector(v),
            });
        }

        await ctx.SaveChangesAsync();

        var query = (await ctx.Chunks.AsNoTracking().FirstAsync(c => c.ChunkIndex == 0)).Embedding!;

        var nearest = await ctx.Chunks
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(query))
            .Take(5)
            .Select(c => c.ChunkIndex)
            .ToListAsync();

        nearest.Should().HaveCount(5);
        nearest[0].Should().Be(0);
    }
}
