using AristaMcp.Core.Retrieval;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Core.Tests.Retrieval;

public class QueryExpanderTests
{
    [Fact]
    public void SingleAcronym_ExpandedOnFirstOccurrence()
    {
        var r = QueryExpander.Expand("EVPN overlay");

        r.Original.Should().Be("EVPN overlay");
        r.Expanded.Should().Be("EVPN (Ethernet VPN) overlay");
    }

    [Fact]
    public void LowercaseAcronym_IsMatched_PreservesOriginalCasing()
    {
        var r = QueryExpander.Expand("mlag configuration");

        r.Expanded.Should().Be("mlag (Multi-chassis Link Aggregation) configuration");
    }

    [Fact]
    public void MultipleAcronyms_EachExpandedOnce()
    {
        var r = QueryExpander.Expand("BGP over VXLAN with EVPN");

        r.Expanded.Should().Be(
            "BGP (Border Gateway Protocol) over VXLAN (Virtual Extensible LAN) with EVPN (Ethernet VPN)");
    }

    [Fact]
    public void RepeatedAcronym_ExpandedOnlyOnce()
    {
        var r = QueryExpander.Expand("EVPN to EVPN handoff");

        r.Expanded.Should().Be("EVPN (Ethernet VPN) to EVPN handoff");
    }

    [Fact]
    public void QueryWithNoKnownAcronyms_IsReturnedUnchanged()
    {
        var r = QueryExpander.Expand("how do I bake a cake");

        r.Expanded.Should().Be("how do I bake a cake");
    }

    [Fact]
    public void PartialMatches_AreNotExpanded()
    {
        // "EVPN5" shouldn't match "EVPN" as a whole word.
        var r = QueryExpander.Expand("EVPN5 deployment");

        r.Expanded.Should().Be("EVPN5 deployment");
    }
}
