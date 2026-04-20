using System.Diagnostics;

namespace AristaMcp.E2E.Helpers;

// Spawns `dotnet arista-mcp.dll <args>` with ARISTA_MCP__ConnectionString + ModelsDir
// wired from the test environment. IAsyncDisposable kills the process on scope exit.
internal sealed class CliProcess : IAsyncDisposable
{
    private readonly Process _process;
    private bool _disposed;

    public StreamReader StdOut => _process.StandardOutput;
    public StreamReader StdErr => _process.StandardError;
    public StreamWriter StdIn => _process.StandardInput;
    public int Id => _process.Id;

    private CliProcess(Process process)
    {
        _process = process;
    }

    public static CliProcess Start(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = E2ETestEnvironment.FindRepoRoot(),
        };

        psi.ArgumentList.Add(E2ETestEnvironment.CliDllPath());
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["ARISTA_MCP__ConnectionString"] = E2ETestEnvironment.ConnectionString;
        psi.Environment["ARISTA_MCP__ModelsDir"] = E2ETestEnvironment.ModelsDir;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to spawn arista-mcp CLI process");
        return new CliProcess(process);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // best-effort teardown — test harness, not production code
        }
        finally
        {
            _process.Dispose();
        }
    }
}
