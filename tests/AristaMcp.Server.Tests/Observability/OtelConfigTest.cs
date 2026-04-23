using AristaMcp.Server.Observability;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AristaMcp.Server.Tests.Observability;

// OtelConfig contract tests: the "unset = no-op" path is the one that MUST
// stay correct — any regression there would turn on exporter threads in every
// arista-mcp serve without warning.
public class OtelConfigTest
{
    [Fact]
    public void IsEnabled_NoEnvVars_ReturnsFalse()
    {
        using (new EnvScope("ARISTA_MCP__Otel__Endpoint", null))
        using (new EnvScope("OTEL_EXPORTER_OTLP_ENDPOINT", null))
        {
            OtelConfig.IsEnabled().Should().BeFalse();
        }
    }

    [Fact]
    public void IsEnabled_AristaSpecificEndpointSet_ReturnsTrue()
    {
        using (new EnvScope("ARISTA_MCP__Otel__Endpoint", "http://localhost:4317"))
        using (new EnvScope("OTEL_EXPORTER_OTLP_ENDPOINT", null))
        {
            OtelConfig.IsEnabled().Should().BeTrue();
        }
    }

    [Fact]
    public void IsEnabled_StandardEndpointSet_ReturnsTrue()
    {
        using (new EnvScope("ARISTA_MCP__Otel__Endpoint", null))
        using (new EnvScope("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317"))
        {
            OtelConfig.IsEnabled().Should().BeTrue();
        }
    }

    [Fact]
    public void AddOtelIfEnabled_Disabled_RegistersNothing()
    {
        using (new EnvScope("ARISTA_MCP__Otel__Endpoint", null))
        using (new EnvScope("OTEL_EXPORTER_OTLP_ENDPOINT", null))
        {
            var services = new ServiceCollection();
            var before = services.Count;
            services.AddOtelIfEnabled();
            services.Count.Should().Be(before,
                "disabled path must not add a single DI entry — that's the contract");
        }
    }

    [Fact]
    public void BuildTracerProviderIfEnabled_Disabled_ReturnsNull()
    {
        using (new EnvScope("ARISTA_MCP__Otel__Endpoint", null))
        using (new EnvScope("OTEL_EXPORTER_OTLP_ENDPOINT", null))
        {
            OtelConfig.BuildTracerProviderIfEnabled().Should().BeNull();
        }
    }

    [Fact]
    public void BuildTracerProviderIfEnabled_Enabled_ReturnsDisposable()
    {
        using (new EnvScope("ARISTA_MCP__Otel__Endpoint", "http://localhost:4317"))
        {
            using var provider = OtelConfig.BuildTracerProviderIfEnabled();
            provider.Should().NotBeNull("endpoint is set — an exporter must spin up");
            // Dispose runs on `using` scope end; the TracerProvider flushes
            // the batch exporter. A missing dispose would keep spans in memory.
        }
    }

    // Scoped env var override — restores the original value on Dispose so tests
    // don't leak global state into the other OtelConfig tests or into downstream
    // integration tests.
    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _original);
        }
    }
}
