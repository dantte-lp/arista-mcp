using System.Text.Json;
using AristaMcp.E2E.Helpers;
using FluentAssertions;
using Xunit;

namespace AristaMcp.E2E;

// End-to-end smoke over the stdio transport:
//   • spawn arista-mcp serve --transport stdio
//   • send raw JSON-RPC initialize + tools/list
//   • assert all 5 tools come back with schemas
//
// SkippableFact — skips when embedder model or ingested data are missing. Wire the
// CLI via ARISTA_MCP__ConnectionString + ARISTA_MCP__ModelsDir from E2ETestEnvironment.
public class StdioTransportE2ETest
{
    [SkippableFact]
    public async Task StdioHandshake_ListsAllFiveTools()
    {
        Skip.IfNot(E2ETestEnvironment.HasEmbedderModel,
            "embedder model not present — run scripts/fetch-models.ps1");
        Skip.IfNot(await E2ETestEnvironment.HasIngestedDataAsync(),
            "no chunks in DB — run `arista-mcp ingest --category manual` first");

        await using var cli = CliProcess.Start("serve", "--transport", "stdio");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await SendAsync(cli, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "arista-mcp-e2e", version = "0.0.1" },
            },
        }, cts.Token);

        var initResp = await ReadJsonRpcAsync(cli, cts.Token);
        initResp.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name")
            .GetString().Should().NotBeNullOrEmpty();

        // initialized notification (no id/response expected)
        await SendAsync(cli, new { jsonrpc = "2.0", method = "notifications/initialized" }, cts.Token);

        await SendAsync(cli, new { jsonrpc = "2.0", id = 2, method = "tools/list" }, cts.Token);
        var listResp = await ReadJsonRpcAsync(cli, cts.Token);

        var tools = listResp.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        tools.Should().BeEquivalentTo(
            "search_docs", "lookup_section", "list_documents", "get_document", "get_status");
    }

    private static async Task SendAsync(CliProcess cli, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await cli.StdIn.WriteLineAsync(json.AsMemory(), ct);
        await cli.StdIn.FlushAsync(ct);
    }

    // Read stdout lines until one parses as a JSON-RPC response. The server logs to
    // stderr per StdioHost — anything on stdout is the transport.
    private static async Task<JsonDocument> ReadJsonRpcAsync(CliProcess cli, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await cli.StdOut.ReadLineAsync(ct);
            if (line is null)
            {
                throw new InvalidOperationException("stdin closed before a JSON-RPC response arrived");
            }

            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            try
            {
                return JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // not a complete JSON document yet — keep reading
            }
        }

        throw new OperationCanceledException(ct);
    }
}
