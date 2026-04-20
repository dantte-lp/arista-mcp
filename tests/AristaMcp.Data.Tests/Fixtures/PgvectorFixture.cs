using System.IO;
using AristaMcp.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace AristaMcp.Data.Tests.Fixtures;

// Shared fixture. Tests get their own isolated database (`arista_test` by default) so
// `ResetAsync()` TRUNCATEs never touch a developer's prod ingest. On first use the
// fixture:
//   1. connects to the maintenance `postgres` DB,
//   2. CREATE DATABASE arista_test IF NOT EXISTS,
//   3. connects to arista_test,
//   4. applies docker/init.sql (extensions + english_analyzer),
//   5. runs EF Core migrations (provisions documents/chunks/ingest_runs + bm25v trigger),
//   6. truncates any leftover rows.
//
// Override the test DB with `ARISTA_MCP_TEST_CS` when running in CI or against a
// differently-located postgres (e.g. the WSL IP workaround).
public sealed class PgvectorFixture : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5434;Database=arista_test;Username=arista;Password=arista";

    public string ConnectionString { get; }
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public PgvectorFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("ARISTA_MCP_TEST_CS")
            ?? DefaultConnectionString;
    }

    public async Task InitializeAsync()
    {
        await EnsureTestDatabaseAsync();

        DataSource = DataSourceFactory.Build(ConnectionString);
        await EnsureExtensionsAsync();

        var opt = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(DataSource, o => o.UseVector())
            .Options;
        await using var ctx = new AristaDbContext(opt);
        await ctx.Database.MigrateAsync();

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

    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE chunks, documents, ingest_runs RESTART IDENTITY CASCADE;";
        await cmd.ExecuteNonQueryAsync();
    }

    // Ensures the test database named in ConnectionString exists. Connects to the
    // maintenance `postgres` DB on the same server. `CREATE DATABASE` cannot run in
    // a transaction; we probe first, then create only if missing.
    private async Task EnsureTestDatabaseAsync()
    {
        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        var dbName = target.Database
            ?? throw new InvalidOperationException("Test connection string must specify Database=");

        // Refuse to reset a prod-looking database by accident.
        if (!dbName.EndsWith("_test", StringComparison.Ordinal)
            && !string.Equals(dbName, "arista_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to run tests against '{dbName}'. Use a DB name ending in '_test' "
                + "(override ARISTA_MCP_TEST_CS if you really need a different name).");
        }

        var admin = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };

        await using var conn = new NpgsqlConnection(admin.ToString());
        await conn.OpenAsync();
        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pg_database WHERE datname = @n";
        probe.Parameters.Add(new NpgsqlParameter<string>("n", dbName));
        var exists = await probe.ExecuteScalarAsync();
        if (exists is null)
        {
            await using var create = conn.CreateCommand();
            // Identifier must be safely escaped — dbName is validated above to a narrow set.
            create.CommandText = $"CREATE DATABASE \"{dbName.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
            await create.ExecuteNonQueryAsync();
        }
    }

    private async Task EnsureExtensionsAsync()
    {
        var path = ResolveRepoFile("docker/init.sql");
        var sql = await File.ReadAllTextAsync(path);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        // init.sql is idempotent for CREATE EXTENSION but create_text_analyzer errors
        // if the analyzer already exists. Swallow that specific class of error.
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
