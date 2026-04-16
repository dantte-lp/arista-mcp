using AristaMcp.Core.Models;
using AristaMcp.Data.Repositories;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class DocumentRepositoryTest(PgvectorFixture fx)
{
    [Fact]
    public async Task UpsertTwice_ResultsInOneRow_LatestWins()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();
        var repo = new DocumentRepository(ctx);

        await repo.UpsertAsync(new AristaDocument
        {
            Id = "u1",
            Url = "u",
            Category = "toi",
            Title = "first",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
            Tags = ["a"],
        }, CancellationToken.None);

        await repo.UpsertAsync(new AristaDocument
        {
            Id = "u1",
            Url = "u",
            Category = "toi",
            Title = "second",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
            Tags = ["a", "b"],
        }, CancellationToken.None);

        var ids = await repo.GetAllIdsAsync(CancellationToken.None);
        ids.Should().ContainSingle().Which.Should().Be("u1");

        var doc = await repo.GetByIdAsync("u1", CancellationToken.None);
        doc!.Title.Should().Be("second");
        doc.Tags.Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task Delete_RemovesDocument()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();
        var repo = new DocumentRepository(ctx);

        await repo.UpsertAsync(new AristaDocument
        {
            Id = "d1",
            Url = "u",
            Category = "toi",
            Title = "t",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        }, CancellationToken.None);

        await repo.DeleteAsync("d1", CancellationToken.None);

        (await repo.GetByIdAsync("d1", CancellationToken.None)).Should().BeNull();
    }
}
