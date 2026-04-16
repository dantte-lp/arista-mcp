using System.IO;
using AristaMcp.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace AristaMcp.Data.Tests.Fixtures;

// Shared fixture: connects to the local podman postgres (docker/compose.yaml) rather than
// spinning a per-run Testcontainer. The postgres two-stage init races with Testcontainers'
// wait strategy on Windows/Podman, and a single shared DB is materially faster for the
// iteration loop. Tests clean up their own rows via ResetAsync().
//
// The `ARISTA_MCP_TEST_CS` environment variable overrides the default connection string
// (for CI or a user who binds postgres on a different host/port).
public sealed class PgvectorFixture : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista";

    public string ConnectionString { get; }
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public PgvectorFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("ARISTA_MCP_TEST_CS")
            ?? DefaultConnectionString;
    }

    public async Task InitializeAsync()
    {
        DataSource = DataSourceFactory.Build(ConnectionString);

        await EnsureExtensionsAsync();

        var opt = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(DataSource, o => o.UseVector())
            .Options;
        await using var ctx = new AristaDbContext(opt);
        await ctx.Database.MigrateAsync();

        await EnsureBm25Async();
        await ResetAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }
    }

    public AristaDbContext CreateContext()
    {
        var opt = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(DataSource, o => o.UseVector())
            .Options;
        return new AristaDbContext(opt);
    }

    // Truncate all test-owned rows; safe to call between tests.
    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE chunks, documents, ingest_runs RESTART IDENTITY CASCADE;";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureExtensionsAsync()
    {
        var path = ResolveRepoFile("docker/init.sql");
        var sql = await File.ReadAllTextAsync(path);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        // init.sql is idempotent for CREATE EXTENSION but create_text_analyzer errors if
        // the analyzer already exists. Swallow that specific class of error.
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (
            string.Equals(ex.SqlState, "XX000", StringComparison.Ordinal)
            || string.Equals(ex.SqlState, "42710", StringComparison.Ordinal))
        {
            _ = ex;
        }
    }

    private async Task EnsureBm25Async()
    {
        // Manually check whether bm25v column + index already exist; apply migration only if not.
        await using var conn = await DataSource.OpenConnectionAsync();
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'chunks' AND column_name = 'bm25v'
                );
                """;
            var exists = (bool?)await probe.ExecuteScalarAsync() ?? false;
            if (exists)
            {
                return;
            }
        }

        var path = ResolveRepoFile("src/AristaMcp.Data/Migrations/Manual/001_bm25v_column.sql");
        var sql = await File.ReadAllTextAsync(path);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string ResolveRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".gitignore"))
                && Directory.Exists(Path.Combine(dir.FullName, "docker")))
            {
                return Path.Combine(dir.FullName, relativePath);
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate repo root while resolving '{relativePath}' from {AppContext.BaseDirectory}");
    }
}
