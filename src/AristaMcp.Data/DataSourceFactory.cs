using Npgsql;

namespace AristaMcp.Data;

public static class DataSourceFactory
{
    // The provisioned container runs `max_connections=20`; Npgsql's default pool ceiling of 100 would
    // let a burst of concurrent searches (each opens up to ~3 physical connections: dense + sparse +
    // hydration) exhaust the server with `53300 too many connections`. Cap the pool below
    // `max_connections` unless the caller already set an explicit size (e.g. the test fixture).
    private const int DefaultMaxPoolSize = 12;

    public static NpgsqlDataSource Build(string connectionString)
    {
        var b = new NpgsqlDataSourceBuilder(CapPoolSize(connectionString));
        b.UseVector();
        return b.Build();
    }

    private static string CapPoolSize(string connectionString)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (!csb.ContainsKey("Maximum Pool Size"))
        {
            csb.MaxPoolSize = DefaultMaxPoolSize;
        }

        return csb.ConnectionString;
    }
}
