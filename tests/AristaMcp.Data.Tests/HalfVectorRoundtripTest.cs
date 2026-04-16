using AristaMcp.Data.Entities;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class HalfVectorRoundtripTest(PgvectorFixture fx)
{
    [Fact]
    public async Task InsertAndReadHalfVector()
    {
        await using var ctx = fx.CreateContext();
        var doc = new DocumentEntity
        {
            Id = "doc1",
            Url = "u",
            Category = "toi",
            Title = "T",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        };
        ctx.Documents.Add(doc);
        await ctx.SaveChangesAsync();

        var vec = new Half[768];
        for (var i = 0; i < 768; i++)
        {
            vec[i] = (Half)(i / 768f);
        }

        var chunk = new ChunkEntity
        {
            DocumentId = "doc1",
            ChunkIndex = 0,
            Content = "title > section\n\nhello world",
            RawContent = "hello world",
            TokenCount = 3,
            Embedding = new HalfVector(vec),
        };
        ctx.Chunks.Add(chunk);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Chunks.AsNoTracking().FirstAsync(c => c.DocumentId == "doc1");
        loaded.Embedding.Should().NotBeNull();
        var arr = loaded.Embedding.ToArray();
        arr.Length.Should().Be(768);
        ((float)arr[100]).Should().BeApproximately(100f / 768f, 1e-2f);
    }
}
