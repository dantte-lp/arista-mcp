using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AristaMcp.E2E.Helpers;
using FluentAssertions;
using Xunit;

namespace AristaMcp.E2E;

// End-to-end smoke over the Streamable HTTP transport:
//   • spawn arista-mcp serve --transport http --port <ephemeral>
//   • wait for Kestrel to accept connections
//   • POST initialize → tools/list; assert response shape
//
// Uses a plain HttpClient so the test is decoupled from the MCP client SDK
// specifics (SSE parsing, retry rules). Server emits text/event-stream with
// `event: message\ndata: {...json-rpc...}\n\n`.
public class HttpTransportE2ETest
{
    [SkippableFact]
    public async Task HttpTransport_InitializeAndToolsList_Succeed()
    {
        Skip.IfNot(E2ETestEnvironment.HasEmbedderModel,
            "embedder model not present — run scripts/fetch-models.ps1");
        Skip.IfNot(await E2ETestEnvironment.HasIngestedDataAsync(),
            "no chunks in DB — run `arista-mcp ingest --category manual` first");

        var port = GetFreePort();
        await using var cli = CliProcess.Start("serve", "--transport", "http", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var baseUrl = $"http://127.0.0.1:{port}/";
        await WaitForReadyAsync(cli, baseUrl, cts.Token);

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var initBody = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"arista-mcp-e2e","version":"0.0.1"}}}
            """;
        var init = await PostAsync(http, initBody, cts.Token);
        init.GetProperty("result").GetProperty("serverInfo").GetProperty("name")
            .GetString().Should().NotBeNullOrEmpty();

        const string listBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";
        var list = await PostAsync(http, listBody, cts.Token);
        var names = list.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        names.Should().BeEquivalentTo(
            "search_docs", "lookup_section", "list_documents", "get_document", "get_status");
    }

    private static async Task WaitForReadyAsync(CliProcess cli, string baseUrl, CancellationToken ct)
    {
        using var probe = new HttpClient { BaseAddress = new Uri(baseUrl) };
        probe.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        probe.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        const string pingBody = """{"jsonrpc":"2.0","id":0,"method":"tools/list"}""";
        while (!ct.IsCancellationRequested)
        {
            if (cli.Id > 0)
            {
                try
                {
                    using var content = new StringContent(pingBody, Encoding.UTF8, "application/json");
                    using var resp = await probe.PostAsync(new Uri("", UriKind.Relative), content, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException) { /* server not up yet */ }
                catch (SocketException) { /* socket not ready */ }
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"HTTP server at {baseUrl} never responded within cts");
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string body, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri("", UriKind.Relative), content, ct);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct);
        var json = ExtractJsonRpcFromSse(text)
            ?? throw new InvalidOperationException($"No JSON-RPC payload found in SSE response: {text}");
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // Streamable HTTP emits text/event-stream frames: "event: message\ndata: {…}\n\n".
    // We need the payload after "data: ".
    private static string? ExtractJsonRpcFromSse(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            const string prefix = "data:";
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..].TrimStart();
            }
        }

        // Plain JSON (non-SSE) — some endpoints return raw.
        var t = text.Trim();
        return t.StartsWith('{') ? t : null;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
