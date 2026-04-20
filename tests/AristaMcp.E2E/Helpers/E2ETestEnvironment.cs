using System.IO;
using Npgsql;

namespace AristaMcp.E2E.Helpers;

// Shared E2E test preconditions + skip helpers. Single source of truth for deciding
// whether the heavy tests can run (models present + DB reachable + has data).
internal static class E2ETestEnvironment
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("ARISTA_MCP_TEST_CS")
        ?? "Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista";

    // Resolves the repo root by walking up from the test binary until we see
    // docker/ + .gitignore (the same heuristic PgvectorFixture uses).
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".gitignore"))
                && Directory.Exists(Path.Combine(dir.FullName, "docker")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate repo root walking up from {AppContext.BaseDirectory}");
    }

    public static string ModelsDir => Path.Combine(FindRepoRoot(), "models");

    public static bool HasEmbedderModel =>
        File.Exists(Path.Combine(ModelsDir, "embedder", "model.onnx"))
        && File.Exists(Path.Combine(ModelsDir, "embedder", "vocab.txt"));

    public static async Task<bool> HasIngestedDataAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunks;";
            var count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    // Path to the built Cli DLL (dotnet build must have run). xUnit test runs after
    // the project's own build, so ProjectReferences are copied to bin/Debug/net10.0/.
    // The CLI's AssemblyName is "arista-mcp" (set in AristaMcp.Cli.csproj).
    public static string CliDllPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "arista-mcp.dll");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // Fallback: locate the Cli project output directly.
        var root = FindRepoRoot();
        var cliBin = Path.Combine(root, "src", "AristaMcp.Cli", "bin", "Debug", "net10.0", "arista-mcp.dll");
        if (File.Exists(cliBin))
        {
            return cliBin;
        }

        throw new FileNotFoundException(
            $"arista-mcp.dll not found; tried {candidate} and {cliBin}");
    }
}
