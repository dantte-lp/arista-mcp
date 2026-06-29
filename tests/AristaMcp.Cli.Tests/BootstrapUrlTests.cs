using AristaMcp.Cli.Commands;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Cli.Tests;

// Regression guard for the pre-v0.3.1 BootstrapCommand URL bug: the template
// used a separate `{version}` placeholder (tag without `v`) while the release
// pipeline names the asset `arista-corpus-${TAG_NAME}.dump` (with `v`). The
// resulting URL 404'd against the real asset.
//
// `BuildCorpusDumpUrl(tag)` is the public stable contract that drives both
// the bootstrap CLI and any external automation that wants to fetch a corpus
// dump for a given release.
public sealed class BootstrapUrlTests
{
    [Theory]
    [InlineData("v0.3.0",
        "https://github.com/dantte-lp/arista-mcp/releases/download/v0.3.0/arista-corpus-v0.3.0.dump")]
    [InlineData("v0.3.1",
        "https://github.com/dantte-lp/arista-mcp/releases/download/v0.3.1/arista-corpus-v0.3.1.dump")]
    [InlineData("v1.0.0-rc.2",
        "https://github.com/dantte-lp/arista-mcp/releases/download/v1.0.0-rc.2/arista-corpus-v1.0.0-rc.2.dump")]
    public void BuildCorpusDumpUrl_ReturnsExpectedAssetUrl(string tag, string expected)
    {
        BootstrapCommand.BuildCorpusDumpUrl(tag).Should().Be(expected);
    }

    [Fact]
    public void BuildCorpusDumpUrl_NormalisesTagWithoutLeadingV()
    {
        // Operator may type `--release 0.3.1`; the helper must re-add `v` so
        // the URL aligns with release.yml's `arista-corpus-${TAG_NAME}.dump`.
        BootstrapCommand.BuildCorpusDumpUrl("0.3.1").Should().Be(
            "https://github.com/dantte-lp/arista-mcp/releases/download/v0.3.1/arista-corpus-v0.3.1.dump");
    }

    [Theory]
    [InlineData("v0.3.1", "v0.3.1")]
    [InlineData("0.3.1", "v0.3.1")]
    [InlineData("v1.0.0-rc.2", "v1.0.0-rc.2")]
    public void NormaliseTag_AddsVPrefixWhenMissing(string input, string expected)
    {
        BootstrapCommand.NormaliseTag(input).Should().Be(expected);
    }
}
