using AristaMcp.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AristaMcp.Server;

public static class StdioHost
{
    public static async Task RunAsync(AristaMcpSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var builder = Host.CreateApplicationBuilder();

        // stdout is the MCP transport; all logs must go to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddAristaMcpServices(settings);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(StdioHost).Assembly);

        using var host = builder.Build();
        await host.RunAsync(ct).ConfigureAwait(false);
    }
}
