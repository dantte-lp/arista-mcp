using System.IO;
using AristaMcp.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace AristaMcp.Data.Tests.Fixtures;

public sealed class PgvectorFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("tensorchord/vchord-suite:pg18-latest")
            .WithDatabase("arista_test")
            .WithUsername("arista")
            .WithPassword("arista")
            .WithCommand(
                "postgres",
                "-c", "shared_preload_libraries=vector,vchord,vchord_bm25,pg_tokenizer",
                "-c", "listen_addresses=*")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await ApplyInitSqlAsync();

        DataSource = DataSourceFactory.Build(ConnectionString);

        var opt = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(DataSource, o => o.UseVector())
            .Options;
        await using var ctx = new AristaDbContext(opt);
        await ctx.Database.MigrateAsync();

        await ApplyBm25MigrationAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public AristaDbContext CreateContext()
    {
        var opt = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(DataSource, o => o.UseVector())
            .Options;
        return new AristaDbContext(opt);
    }

    private async Task ApplyInitSqlAsync()
    {
        var path = ResolveRepoFile("docker/init.sql");
        var sql = await File.ReadAllTextAsync(path);
        // init.sql targets the `arista` database via `ALTER DATABASE arista`. The test
        // database is `arista_test` — rewrite the statements so they apply correctly.
        sql = sql.Replace("ALTER DATABASE arista ", "ALTER DATABASE arista_test ", StringComparison.Ordinal);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ApplyBm25MigrationAsync()
    {
        var path = ResolveRepoFile("src/AristaMcp.Data/Migrations/Manual/001_bm25v_column.sql");
        var sql = await File.ReadAllTextAsync(path);

        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string ResolveRepoFile(string relativePath)
    {
        // Walk up from the test assembly location until a .gitignore (repo root marker) is found.
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
