using System.CommandLine;
using AristaMcp.Cli.Configuration;
using AristaMcp.Core.Settings;
using AristaMcp.Server;

namespace AristaMcp.Cli.Commands;

public static class ServeCommand
{
    public static Command Build()
    {
        var transport = new Option<string>("--transport")
        {
            Description = "Transport: 'stdio' (MCP over stdio, default) or 'http' (Streamable HTTP)",
            DefaultValueFactory = _ => "stdio",
        };

        var port = new Option<int>("--port")
        {
            Description = "HTTP port (ignored for stdio transport)",
            DefaultValueFactory = _ => 8080,
        };

        var bind = new Option<string>("--bind")
        {
            Description = "HTTP bind address. Default 127.0.0.1; pass 0.0.0.0 inside containers.",
            DefaultValueFactory = _ => "127.0.0.1",
        };

        var cmd = new Command("serve", "Run the arista-mcp MCP server")
        {
            transport,
            port,
            bind,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var t = pr.GetValue(transport) ?? "stdio";
            var p = pr.GetValue(port);
            var b = pr.GetValue(bind) ?? "127.0.0.1";
            var settings = CliConfiguration.Load();

            try
            {
                if (string.Equals(t, "stdio", StringComparison.OrdinalIgnoreCase))
                {
                    await StdioHost.RunAsync(settings, ct).ConfigureAwait(false);
                }
                else if (string.Equals(t, "http", StringComparison.OrdinalIgnoreCase))
                {
                    await HttpHost.RunAsync(settings, b, p, ct).ConfigureAwait(false);
                }
                else
                {
                    Console.Error.WriteLine($"Unknown transport '{t}' — expected 'stdio' or 'http'");
                    return 2;
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        });

        return cmd;
    }
}
