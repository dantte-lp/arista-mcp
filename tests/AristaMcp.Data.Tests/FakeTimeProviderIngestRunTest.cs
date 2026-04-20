using AristaMcp.Data.Repositories;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class FakeTimeProviderIngestRunTest(PgvectorFixture fx)
{
    [Fact]
    public async Task StartThenFinish_ReportsExactElapsedFromFakeClock()
    {
        await fx.ResetAsync();

        var t0 = new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(t0);

        await using var ctx = fx.CreateContext();
        var repo = new IngestRunRepository(ctx, clock);

        var run = await repo.StartAsync("sha-fake", CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(5));
        await repo.FinishAsync(run.Id, "success", 10, 2, 8, 42, null, CancellationToken.None);

        var last = await repo.GetLastAsync(CancellationToken.None);

        last.Should().NotBeNull();
        last!.StartedAt.Should().Be(t0);
        last.FinishedAt.Should().Be(t0 + TimeSpan.FromMinutes(5));
        (last.FinishedAt!.Value - last.StartedAt).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task MultipleAdvances_AccumulateOnFinishTimestamp()
    {
        await fx.ResetAsync();

        var t0 = new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(t0);

        await using var ctx = fx.CreateContext();
        var repo = new IngestRunRepository(ctx, clock);

        var run = await repo.StartAsync("sha-multi", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(90));
        clock.Advance(TimeSpan.FromSeconds(30));
        await repo.FinishAsync(run.Id, "success", 1, 0, 1, 3, null, CancellationToken.None);

        var last = await repo.GetLastAsync(CancellationToken.None);

        // FakeTimeProvider accumulates advances; StartedAt is frozen at t0.
        last!.FinishedAt.Should().Be(t0 + TimeSpan.FromSeconds(120));
    }
}
