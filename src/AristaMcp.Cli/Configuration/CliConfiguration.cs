using AristaMcp.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace AristaMcp.Cli.Configuration;

public static class CliConfiguration
{
    public const string EnvPrefix = "ARISTA_MCP__";

    public static AristaMcpSettings Load()
    {
        var builder = new ConfigurationBuilder()
            .AddEnvironmentVariables(EnvPrefix);

        var cfgFile = Path.Combine(Environment.CurrentDirectory, "arista-mcp.json");
        if (File.Exists(cfgFile))
        {
            builder.AddJsonFile(cfgFile, optional: true, reloadOnChange: false);
        }

        var root = builder.Build();
        return root.Get<AristaMcpSettings>() ?? new AristaMcpSettings();
    }
}
