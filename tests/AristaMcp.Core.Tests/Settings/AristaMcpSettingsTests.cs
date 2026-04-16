using AristaMcp.Core.Settings;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AristaMcp.Core.Tests.Settings;

public class AristaMcpSettingsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var s = new AristaMcpSettings();

        s.EmbeddingModel.Should().Be("snowflake-arctic-embed-m-v1.5");
        s.EmbeddingDim.Should().Be(768);
        s.Transport.Should().Be(McpTransport.Stdio);
        s.HttpPort.Should().Be(8080);
        s.Gpu.Should().BeFalse();
    }

    [Fact]
    public void BindsFromConfiguration()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ARISTA_MCP:ConnectionString"] = "Host=x",
                ["ARISTA_MCP:Gpu"] = "true",
                ["ARISTA_MCP:HttpPort"] = "9090",
            })
            .Build();

        var s = cfg.GetSection("ARISTA_MCP").Get<AristaMcpSettings>()!;

        s.ConnectionString.Should().Be("Host=x");
        s.Gpu.Should().BeTrue();
        s.HttpPort.Should().Be(9090);
    }
}
