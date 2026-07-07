using AristaMcp.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace AristaMcp.Cli.Configuration;

public static class CliConfiguration
{
    public const string EnvPrefix = "ARISTA_MCP__";

    // Provider precedence is "last registered wins" in
    // Microsoft.Extensions.Configuration. We add the optional JSON file FIRST so
    // ARISTA_MCP__* environment variables — set by systemd EnvironmentFile,
    // Quadlet `Environment=`, container runtime, etc. — override a developer's
    // local arista-mcp.json. Same shape as ASP.NET Core's default ordering
    // (appsettings.json → env vars).
    public static AristaMcpSettings Load()
    {
        var builder = new ConfigurationBuilder();

        var cfgFile = Path.Combine(Environment.CurrentDirectory, "arista-mcp.json");
        if (File.Exists(cfgFile))
        {
            builder.AddJsonFile(cfgFile, optional: true, reloadOnChange: false);
        }

        builder.AddEnvironmentVariables(EnvPrefix);

        var root = builder.Build();
        var settings = root.Get<AristaMcpSettings>() ?? new AristaMcpSettings();
        settings.Validate();
        return settings;
    }
}
