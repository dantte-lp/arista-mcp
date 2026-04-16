using Npgsql;

namespace AristaMcp.Data;

public static class DataSourceFactory
{
    public static NpgsqlDataSource Build(string connectionString)
    {
        var b = new NpgsqlDataSourceBuilder(connectionString);
        b.UseVector();
        return b.Build();
    }
}
