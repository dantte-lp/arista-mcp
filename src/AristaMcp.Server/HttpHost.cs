using AristaMcp.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AristaMcp.Server;

public static class HttpHost
{
    public static Task RunAsync(AristaMcpSettings settings, string bindAddress, int port, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindAddress);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{bindAddress}:{port}");

        builder.Services.AddAristaMcpServices(settings);

        // Liveness probe for orchestrators (Podman HealthCmd, Kubernetes,
        // Windows Service watchdog). The check is process-liveness only —
        // backend dependency status (DB reachable, models loaded) is reported
        // separately by the `get_status` MCP tool. Mirrors nutanix-mcp.
        builder.Services.AddHealthChecks();

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithToolsFromAssembly(typeof(HttpHost).Assembly);

        var app = builder.Build();
        app.MapMcp();
        app.MapHealthChecks("/v1/healthz");

        return app.RunAsync(ct);
    }
}
