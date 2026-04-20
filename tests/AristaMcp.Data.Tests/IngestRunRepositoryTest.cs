using AristaMcp.Data.Repositories;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class IngestRunRepositoryTest(PgvectorFixture fx)
{
    [Fact]
    public async Task StartThenFinish_TracksStatusAndCounts()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();
        var repo = new IngestRunRepository(ctx, TimeProvider.System);

        var run = await repo.StartAsync("abc123", CancellationToken.None);
        run.Id.Should().BeGreaterThan(0);
        run.Status.Should().Be("running");

        await repo.FinishAsync(run.Id, "success", 5, 1, 4, 42, null, CancellationToken.None);

        var last = await repo.GetLastAsync(CancellationToken.None);
        last.Should().NotBeNull();
        last!.Id.Should().Be(run.Id);
        last.Status.Should().Be("success");
        last.DocsUpserted.Should().Be(4);
        last.ChunksUpserted.Should().Be(42);
    }

    [Fact]
    public async Task GetLastSuccessfulCatalogSha_IgnoresRunningAndFailedRuns()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();
        var repo = new IngestRunRepository(ctx, TimeProvider.System);

        var good = await repo.StartAsync("GOOD", CancellationToken.None);
        await repo.FinishAsync(good.Id, "success", 1, 0, 1, 3, null, CancellationToken.None);

        var bad = await repo.StartAsync("BAD", CancellationToken.None);
        await repo.FinishAsync(bad.Id, "error", 1, 0, 0, 0, "boom", CancellationToken.None);

        var running = await repo.StartAsync("LATER", CancellationToken.None);
        _ = running;

        var lastSha = await repo.GetLastSuccessfulCatalogSha256Async(CancellationToken.None);

        lastSha.Should().Be("GOOD");
    }
}
