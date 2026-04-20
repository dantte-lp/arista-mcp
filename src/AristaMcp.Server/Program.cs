namespace AristaMcp.Server;

// The Server assembly is hosted via AristaMcp.Cli; this entrypoint only exists to
// satisfy the Web SDK. Use `arista-mcp serve --transport stdio|http` instead.
internal static class Program
{
    public static int Main(string[] args)
    {
        _ = args;
        Console.Error.WriteLine("Run via: arista-mcp serve --transport stdio|http");
        return 0;
    }
}
