using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AristaMcp.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AristaDbContext>
{
    public AristaDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ARISTA_MCP_CS")
            ?? "Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista";
        var ds = DataSourceFactory.Build(cs);
        var opt = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(ds, o => o.UseVector())
            .Options;
        return new AristaDbContext(opt);
    }
}
