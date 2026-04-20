using System.CommandLine;
using AristaMcp.Cli.Commands;

namespace AristaMcp.Cli;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        var root = new RootCommand("arista-mcp — MCP server + CLI for Arista documentation search")
        {
            IngestCommand.Build(),
        };

        return root.Parse(args).InvokeAsync();
    }
}
