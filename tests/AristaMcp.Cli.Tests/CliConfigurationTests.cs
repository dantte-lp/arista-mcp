using System;
using System.IO;
using AristaMcp.Cli.Configuration;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Cli.Tests;

// Regression guard for the v0.3.x code-review F3 / S1 finding:
// `Microsoft.Extensions.Configuration` resolves keys by "last registered
// provider wins". `CliConfiguration` MUST add the JSON file before the env-var
// provider so production overrides via `ARISTA_MCP__*` always take precedence
// over a stray developer `arista-mcp.json` in the working directory.
public sealed class CliConfigurationTests : IDisposable
{
    private readonly string _previousCwd = Environment.CurrentDirectory;
    private readonly string _scratchDir;
    private const string EnvKeyModelsDir = "ARISTA_MCP__ModelsDir";
    private const string EnvKeyConnString = "ARISTA_MCP__ConnectionString";

    public CliConfigurationTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "arista-cli-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_scratchDir);
        Environment.CurrentDirectory = _scratchDir;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _previousCwd;
        Environment.SetEnvironmentVariable(EnvKeyModelsDir, null);
        Environment.SetEnvironmentVariable(EnvKeyConnString, null);
        try { Directory.Delete(_scratchDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public void EnvironmentVariable_overrides_JsonFile_value()
    {
        File.WriteAllText(
            Path.Combine(_scratchDir, "arista-mcp.json"),
            """{ "ModelsDir": "from-json", "ConnectionString": "Host=json" }""");

        Environment.SetEnvironmentVariable(EnvKeyModelsDir, "from-env");
        Environment.SetEnvironmentVariable(EnvKeyConnString, "Host=env");

        var settings = CliConfiguration.Load();

        settings.ModelsDir.Should().Be("from-env",
            "ARISTA_MCP__* env vars must win over arista-mcp.json (12-factor); " +
            "see code-review finding S1 / N-F3.");
        settings.ConnectionString.Should().Be("Host=env");
    }

    [Fact]
    public void JsonFile_used_when_no_environment_variable_set()
    {
        File.WriteAllText(
            Path.Combine(_scratchDir, "arista-mcp.json"),
            """{ "ModelsDir": "from-json" }""");

        Environment.SetEnvironmentVariable(EnvKeyModelsDir, null);

        var settings = CliConfiguration.Load();

        settings.ModelsDir.Should().Be("from-json");
    }

    [Fact]
    public void Defaults_when_neither_source_present()
    {
        Environment.SetEnvironmentVariable(EnvKeyModelsDir, null);

        var settings = CliConfiguration.Load();

        // Default from AristaMcpSettings — sanity-check that the binder didn't
        // wipe defaults when both sources are empty.
        settings.ModelsDir.Should().Be("models");
    }
}
