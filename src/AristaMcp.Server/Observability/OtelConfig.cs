using AristaMcp.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AristaMcp.Server.Observability;

// Opt-in OTLP exporter for the AristaActivity source. Off by default; turns on
// when any of these env vars is set:
//   ARISTA_MCP__Otel__Endpoint    — arista-specific knob (preferred)
//   OTEL_EXPORTER_OTLP_ENDPOINT   — OTEL standard; honoured by the exporter
//                                   natively, so setting it alone ALSO works
//                                   (we detect it to trigger registration).
// When neither is set, no DI registration happens → zero allocation, zero
// exporter threads, zero effect on the serve/ingest hot paths.
public static class OtelConfig
{
    // Same env prefix as CliConfiguration; kept duplicated here to avoid a
    // back-reference from Server → Cli. The double-underscore is the
    // Microsoft.Extensions.Configuration convention for nested keys.
    private const string AristaOtelEndpointEnv = "ARISTA_MCP__Otel__Endpoint";
    private const string StandardOtelEndpointEnv = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public const string ServiceName = "arista-mcp";

    public static bool IsEnabled() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AristaOtelEndpointEnv))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StandardOtelEndpointEnv));

    public static IServiceCollection AddOtelIfEnabled(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!IsEnabled())
        {
            return services;
        }

        var aristaEndpoint = Environment.GetEnvironmentVariable(AristaOtelEndpointEnv);

        services.AddOpenTelemetry()
            .ConfigureResource(b => b
                .AddService(serviceName: ServiceName, serviceVersion: AristaActivity.Version)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("arista.component", "server"),
                }))
            .WithTracing(tracing =>
            {
                tracing.AddSource(AristaActivity.SourceName);
                if (!string.IsNullOrWhiteSpace(aristaEndpoint))
                {
                    tracing.AddOtlpExporter(opts => opts.Endpoint = new Uri(aristaEndpoint));
                }
                else
                {
                    // Fall through to OTEL_EXPORTER_OTLP_ENDPOINT env var —
                    // the exporter reads it itself via its default options.
                    tracing.AddOtlpExporter();
                }
            });

        return services;
    }

    // Imperative companion for CLI commands that don't build a host (bench,
    // ingest, curate-triples). AddOpenTelemetry + WithTracing only start the
    // exporter when an IHostedService lifecycle runs the TracerProvider, so
    // short-lived CLI commands need to build + dispose explicitly.
    //
    // Returns IDisposable or null. Caller: `using var _ = OtelConfig.BuildTracerProviderIfEnabled();`.
    // Dispose flushes the batch exporter.
    public static IDisposable? BuildTracerProviderIfEnabled()
    {
        if (!IsEnabled())
        {
            return null;
        }

        var aristaEndpoint = Environment.GetEnvironmentVariable(AristaOtelEndpointEnv);

        var builder = Sdk.CreateTracerProviderBuilder()
            .ConfigureResource(b => b
                .AddService(serviceName: ServiceName, serviceVersion: AristaActivity.Version)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("arista.component", "cli"),
                }))
            .AddSource(AristaActivity.SourceName);

        if (!string.IsNullOrWhiteSpace(aristaEndpoint))
        {
            builder.AddOtlpExporter(opts => opts.Endpoint = new Uri(aristaEndpoint));
        }
        else
        {
            builder.AddOtlpExporter();
        }

        return builder.Build();
    }
}
