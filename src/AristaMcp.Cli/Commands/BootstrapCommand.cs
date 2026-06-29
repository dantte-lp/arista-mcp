using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Spectre.Console;

namespace AristaMcp.Cli.Commands;

// Single-command bootstrap of a working arista-mcp install:
//
//   1. Detect platform (Windows / Linux / macOS) and container runtime
//      (podman preferred, docker fallback).
//   2. Stage the Postgres container (or verify a running one).
//   3. Stream the corpus dump from a GitHub release attachment and
//      pg_restore inside the container.
//   4. (Linux + --quadlet) Drop Quadlet unit files into
//      ~/.config/containers/systemd/, then `systemctl --user
//      daemon-reload && enable --now`.
//
// Goal: zero manual steps between `arista-mcp serve` and a working
// instance with a populated corpus. The only remaining requirement is
// model fetch (handled by `scripts/fetch-models.ps1`).
public static class BootstrapCommand
{
    private const string DefaultPgImage = "docker.io/tensorchord/vchord-suite:pg18-latest";
    private const string DefaultContainerName = "arista-mcp-postgres";
    private const string DefaultDatabase = "arista";
    private const string DefaultUser = "arista";
    // Default first-boot password for the PG container. The bootstrap flow
    // is dev-grade — operators rotate via `ALTER USER arista PASSWORD '…'`
    // before exposing the listener beyond 127.0.0.1. The value lives here
    // so the constant is grep-able for security review (not in a hidden
    // env-default).
#pragma warning disable S2068 // Hard-coded credential — see docstring above.
    private const string DefaultPgPassword = "arista";
#pragma warning restore S2068
    private const int DefaultHostPort = 5434;

    // Release-attachment URL pattern. Tag is interpolated; the asset
    // is the dump produced by the release pipeline.
    private static readonly System.Text.CompositeFormat s_releaseDumpAssetTemplate =
        System.Text.CompositeFormat.Parse(
            "https://github.com/dantte-lp/arista-mcp/releases/download/{0}/arista-corpus-{1}.dump");

    public static Command Build()
    {
        var release = new Option<string>("--release")
        {
            Description = "Release tag whose `arista-corpus-<tag>.dump` asset to restore (default: skip restore).",
        };
        var quadlet = new Option<bool>("--quadlet")
        {
            Description = "Linux: install Podman Quadlet unit files for auto-restart on boot.",
        };
        var pgImage = new Option<string>("--pg-image")
        {
            Description = "Override the Postgres image reference.",
            DefaultValueFactory = _ => DefaultPgImage,
        };
        var containerName = new Option<string>("--container-name")
        {
            Description = "Postgres container name.",
            DefaultValueFactory = _ => DefaultContainerName,
        };
        var hostPort = new Option<int>("--host-port")
        {
            Description = "Host port to publish 5432 on.",
            DefaultValueFactory = _ => DefaultHostPort,
        };
        var skipPg = new Option<bool>("--skip-pg")
        {
            Description = "Don't touch the Postgres container — assume it's already running.",
        };
        var skipRestore = new Option<bool>("--skip-restore")
        {
            Description = "Provision Postgres but skip the corpus restore.",
        };

        var cmd = new Command("bootstrap",
            "One-shot install: provision Postgres, restore corpus dump from a release attachment, optionally wire systemd Quadlet for auto-restart.")
        {
            release,
            quadlet,
            pgImage,
            containerName,
            hostPort,
            skipPg,
            skipRestore,
        };

        cmd.SetAction(async (pr, ct) =>
        {
            var console = AnsiConsole.Console;
            var args = new BootstrapArgs(
                Release: pr.GetValue(release),
                Quadlet: pr.GetValue(quadlet),
                PgImage: pr.GetValue(pgImage) ?? DefaultPgImage,
                ContainerName: pr.GetValue(containerName) ?? DefaultContainerName,
                HostPort: pr.GetValue(hostPort),
                SkipPg: pr.GetValue(skipPg),
                SkipRestore: pr.GetValue(skipRestore));

            return await RunAsync(console, args, ct).ConfigureAwait(false);
        });

        return cmd;
    }

    private sealed record BootstrapArgs(
        string? Release,
        bool Quadlet,
        string PgImage,
        string ContainerName,
        int HostPort,
        bool SkipPg,
        bool SkipRestore);

    private static async Task<int> RunAsync(IAnsiConsole console, BootstrapArgs args, CancellationToken ct)
    {
        console.MarkupLine("[bold]arista-mcp bootstrap[/]");

        // Step 1: detect runtime.
        string? runtime = await DetectRuntimeAsync(ct).ConfigureAwait(false);
        if (runtime is null)
        {
            console.MarkupLine("[red]error[/] neither `podman` nor `docker` found in PATH");
            return 2;
        }
        console.MarkupLineInterpolated(CultureInfo.InvariantCulture, $"  runtime  [green]{runtime}[/]");
        console.MarkupLineInterpolated(CultureInfo.InvariantCulture, $"  platform [green]{RuntimePlatform()}[/]");

        // Step 2: provision PG.
        if (!args.SkipPg)
        {
            int rc = await EnsurePostgresAsync(runtime, args, console, ct).ConfigureAwait(false);
            if (rc != 0)
            {
                return rc;
            }
        }
        else
        {
            console.MarkupLine("  pg       [yellow]skipped[/] (--skip-pg)");
        }

        // Step 3: restore corpus.
        if (!args.SkipRestore && args.Release is not null)
        {
            int rc = await RestoreCorpusAsync(runtime, args, console, ct).ConfigureAwait(false);
            if (rc != 0)
            {
                return rc;
            }
        }
        else if (args.Release is null)
        {
            console.MarkupLine("  corpus   [yellow]skipped[/] (no --release given; ingest manually with `arista-mcp ingest`)");
        }
        else
        {
            console.MarkupLine("  corpus   [yellow]skipped[/] (--skip-restore)");
        }

        // Step 4: Quadlet (Linux only).
        if (args.Quadlet)
        {
            if (!OperatingSystem.IsLinux())
            {
                console.MarkupLine("[yellow]warn[/] --quadlet is Linux-only; skipping (use deploy/windows/Install-AristaMcpService.ps1 on Windows).");
            }
            else
            {
                int rc = await InstallQuadletAsync(console, ct).ConfigureAwait(false);
                if (rc != 0)
                {
                    return rc;
                }
            }
        }

        console.MarkupLine("[bold green]bootstrap done.[/]");
        return 0;
    }

    private static async Task<string?> DetectRuntimeAsync(CancellationToken ct)
    {
        foreach (string candidate in new[] { "podman", "docker" })
        {
            int code = await RunSilentAsync(candidate, "--version", ct).ConfigureAwait(false);
            if (code == 0)
            {
                return candidate;
            }
        }
        return null;
    }

    private static string RuntimePlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }
        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }
        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }
        return "unknown";
    }

    private static async Task<int> EnsurePostgresAsync(
        string runtime, BootstrapArgs args, IAnsiConsole console, CancellationToken ct)
    {
        // `podman container exists` returns exit 0/1; the Docker CLI has no such
        // subcommand. Use `ps -aq --filter name=^X$` instead — works on both
        // runtimes; non-empty stdout means the container exists.
        string existing = await RunSilentReadStdoutAsync(
            runtime, $"ps -aq --filter name=^{args.ContainerName}$", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                $"  pg       [green]container exists[/]: {args.ContainerName} (starting if stopped)");
            await RunSilentAsync(runtime, $"start {args.ContainerName}", ct).ConfigureAwait(false);
            return 0;
        }

        console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
            $"  pg       creating container [green]{args.ContainerName}[/] from {args.PgImage}");
        // Pull first so the run step doesn't time out on slow links.
        await RunStreamingAsync(runtime, $"pull {args.PgImage}", console, ct).ConfigureAwait(false);

        // Mount the repo's init.sql if available alongside the binary
        // (release artefacts include docker/init.sql at $publishdir/docker/).
        string? initSql = LocateInitSql();
        string initMount = initSql is null
            ? string.Empty
            : $" -v \"{initSql}:/docker-entrypoint-initdb.d/01-init.sql:ro\"";

#pragma warning disable S2068 // Const credential — see DefaultPgPassword docstring.
        string runArgs = string.Join(' ',
            "run -d",
            $"--name {args.ContainerName}",
            "--restart unless-stopped",
            $"-p 127.0.0.1:{args.HostPort.ToString(CultureInfo.InvariantCulture)}:5432",
            $"-e POSTGRES_DB={DefaultDatabase}",
            $"-e POSTGRES_USER={DefaultUser}",
            $"-e POSTGRES_PASSWORD={DefaultPgPassword}",
            "-e POSTGRES_INITDB_ARGS=\"--data-checksums --encoding=UTF-8 --locale=C.UTF-8\"",
            $"-v {args.ContainerName}-data:/var/lib/postgresql/data",
            initMount,
            args.PgImage);
#pragma warning restore S2068

        int code = await RunStreamingAsync(runtime, runArgs, console, ct).ConfigureAwait(false);
        if (code != 0)
        {
            console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                $"[red]error[/] {runtime} run failed (exit {code})");
            return code;
        }

        // Wait for pg_isready.
        for (int attempt = 1; attempt <= 30; attempt++)
        {
            int rc = await RunSilentAsync(runtime,
                $"exec {args.ContainerName} pg_isready -U {DefaultUser} -d {DefaultDatabase}",
                ct).ConfigureAwait(false);
            if (rc == 0)
            {
                console.MarkupLine("  pg       [green]ready[/]");
                return 0;
            }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
        console.MarkupLine("[red]error[/] postgres did not become ready in 60 s");
        return 3;
    }

    private static async Task<int> RestoreCorpusAsync(
        string runtime, BootstrapArgs args, IAnsiConsole console, CancellationToken ct)
    {
        if (args.Release is null)
        {
            return 0;
        }

        string tag = args.Release;
        string version = tag.StartsWith('v') ? tag[1..] : tag;
        string url = string.Format(CultureInfo.InvariantCulture, s_releaseDumpAssetTemplate, tag, version);
        // The URL is built from a fixed CompositeFormat with our own
        // tag/version inputs, so it cannot escape https://github.com/.

        console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
            $"  corpus   downloading [green]{url}[/]");

        string tmpHostPath = Path.Combine(Path.GetTempPath(), $"arista-corpus-{version}.dump");
        try
        {
            await DownloadFileAsync(url, tmpHostPath, console, ct).ConfigureAwait(false);

            // Copy into the container.
            await RunStreamingAsync(runtime, $"cp \"{tmpHostPath}\" \"{args.ContainerName}:/tmp/arista-corpus.dump\"",
                console, ct).ConfigureAwait(false);

            console.MarkupLine("  corpus   running pg_restore inside the container");
            // --clean drops existing schema, --if-exists tolerates a fresh DB.
            // -j 4 parallelises restore; HNSW index rebuild may still serialize.
#pragma warning disable S2068 // Const credential — see DefaultPgPassword docstring.
            string restoreArgs = string.Join(' ',
                $"exec -e PGPASSWORD={DefaultPgPassword} {args.ContainerName}",
                $"pg_restore -h 127.0.0.1 -U {DefaultUser} -d {DefaultDatabase}",
                "--no-owner --no-acl --clean --if-exists -j 4",
                "/tmp/arista-corpus.dump");
#pragma warning restore S2068
            int code = await RunStreamingAsync(runtime, restoreArgs, console, ct).ConfigureAwait(false);
            if (code != 0)
            {
                console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                    $"[yellow]warn[/] pg_restore exit {code} — typical for HNSW shared-mem ENOSPC; retrying serially");
                // Drop+recreate the HNSW index serially with no parallel
                // workers (avoids /dev/shm exhaustion on small containers).
                string hnswSql = "SET maintenance_work_mem = '4GB'; SET max_parallel_maintenance_workers = 0; " +
                    "DROP INDEX IF EXISTS \\\"IX_chunks_embedding\\\"; " +
                    "CREATE INDEX \\\"IX_chunks_embedding\\\" ON chunks USING hnsw (embedding halfvec_cosine_ops) " +
                    "WITH (ef_construction=200, m=16);";
#pragma warning disable S2068
                int hnswRc = await RunStreamingAsync(runtime,
                    $"exec -e PGPASSWORD={DefaultPgPassword} {args.ContainerName} " +
                    $"psql -U {DefaultUser} -d {DefaultDatabase} -c \"{hnswSql}\"",
                    console, ct).ConfigureAwait(false);
#pragma warning restore S2068
                if (hnswRc != 0)
                {
                    console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                        $"[red]error[/] serial HNSW index rebuild failed (exit {hnswRc}); dense search will return no results until the index is rebuilt manually.");
                    return hnswRc;
                }
            }
            console.MarkupLine("  corpus   [green]restored[/]");
            return 0;
        }
        finally
        {
            // Cleanup must happen even on download / pg_restore failure — otherwise
            // a partial dump leaks into Path.GetTempPath() and into the container's
            // /tmp on repeated bootstrap attempts. Use a fresh CancellationToken so
            // a cancelled outer operation does not block cleanup itself.
            await RunSilentAsync(runtime,
                $"exec {args.ContainerName} rm -f /tmp/arista-corpus.dump", CancellationToken.None).ConfigureAwait(false);
            if (File.Exists(tmpHostPath))
            {
                try { File.Delete(tmpHostPath); }
                catch (IOException) { /* best-effort cleanup */ }
                catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
            }
        }
    }

    private static async Task<int> InstallQuadletAsync(IAnsiConsole console, CancellationToken ct)
    {
        string home = Environment.GetEnvironmentVariable("HOME")
                      ?? throw new InvalidOperationException("HOME unset");
        string quadletDir = Path.Combine(home, ".config", "containers", "systemd");
        Directory.CreateDirectory(quadletDir);

        string? repoQuadlet = LocateRepoFile(Path.Combine("deploy", "quadlet"));
        if (repoQuadlet is null || !Directory.Exists(repoQuadlet))
        {
            console.MarkupLine("[red]error[/] deploy/quadlet/ not found alongside the binary");
            return 4;
        }
        foreach (string src in Directory.EnumerateFiles(repoQuadlet)
                     .Where(f => f.EndsWith(".container", StringComparison.Ordinal)
                              || f.EndsWith(".network", StringComparison.Ordinal)
                              || f.EndsWith(".volume", StringComparison.Ordinal)))
        {
            string dst = Path.Combine(quadletDir, Path.GetFileName(src));
            File.Copy(src, dst, overwrite: true);
            console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                $"  quadlet  installed [green]{Path.GetFileName(dst)}[/]");
        }

        await RunStreamingAsync("systemctl", "--user daemon-reload", console, ct).ConfigureAwait(false);
        await RunStreamingAsync("systemctl",
            "--user enable --now arista-mcp-postgres.service", console, ct).ConfigureAwait(false);

        console.MarkupLine("  quadlet  [green]enabled[/]");
        console.MarkupLine("  next: `systemctl --user enable --now arista-mcp.service` once the server image lands on ghcr.io.");
        return 0;
    }

    private static string? LocateInitSql()
    {
        string? repoFile = LocateRepoFile(Path.Combine("docker", "init.sql"));
        return repoFile;
    }

    private static string? LocateRepoFile(string relative)
    {
        string baseDir = AppContext.BaseDirectory;
        var probe = new DirectoryInfo(baseDir);
        while (probe is not null)
        {
            string candidate = Path.Combine(probe.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
            probe = probe.Parent;
        }
        return null;
    }

    private static async Task DownloadFileAsync(string url, string destination, IAnsiConsole console, CancellationToken ct)
    {
        // Validate scheme defensively — only allow https.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"refusing to download from non-https URL: {url}");
        }
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30),
            DefaultRequestVersion = new Version(2, 0),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"arista-mcp/{Assembly.GetExecutingAssembly().GetName().Version}");
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(destination);
        var buf = new byte[256 * 1024];
        long copied = 0;
        long lastReport = 0;
        int n;
        while ((n = await input.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            copied += n;
            if (copied - lastReport > 50 * 1024 * 1024 || (total.HasValue && copied == total))
            {
                lastReport = copied;
                if (total.HasValue)
                {
                    double pct = copied * 100.0 / total.Value;
                    console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                        $"           {copied / 1024 / 1024} / {total / 1024 / 1024} MB ({pct:F1}%)");
                }
                else
                {
                    console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                        $"           {copied / 1024 / 1024} MB");
                }
            }
        }
    }

    private static async Task<int> RunSilentAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return -1;
            }
            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Don't leave a half-finished podman exec / pg_isready process
                // running in the background — kill the whole tree so the next
                // bootstrap attempt isn't blocked by a stale child.
                try { p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                throw;
            }
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Executable not found.
            return -1;
        }
    }

    // Variant of RunSilentAsync that captures stdout — needed because we use
    // `docker ps -q --filter name=…` for portable container-existence checks:
    // both podman and docker exit 0 when ps succeeds, and we have to inspect
    // stdout (empty == not found, container-id == found) to tell the cases apart.
    private static async Task<string> RunSilentReadStdoutAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return string.Empty;
            }
            // Read stdout before waiting for exit so we don't deadlock if the
            // child writes more than the pipe buffer.
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                throw;
            }
            return p.ExitCode == 0
                ? (await stdoutTask.ConfigureAwait(false)).Trim()
                : string.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    private static async Task<int> RunStreamingAsync(
        string file, string args, IAnsiConsole console, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to launch {file}");
        p.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                    $"           {Markup.Escape(e.Data)}");
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                    $"           [yellow]{Markup.Escape(e.Data)}[/]");
            }
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kill the whole tree so an interrupted pg_restore / podman pull
            // doesn't keep running past the abandoned bootstrap.
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            throw;
        }
        return p.ExitCode;
    }
}
