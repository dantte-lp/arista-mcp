using AristaMcp.Core.Retrieval;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Core.Tests.Retrieval;

public class MultiQueryExpanderTests
{
    private static readonly MultiQueryExpander Sut = new();

    [Fact]
    public void OriginalAlwaysFirst()
    {
        var result = Sut.Expand("plain query");
        result.Should().NotBeEmpty();
        result[0].Should().Be("plain query");
    }

    [Fact]
    public void DropsParenAnnotationsAndEmitsContractedVariant()
    {
        var input = "MLAG (Multi-chassis Link Aggregation) peer-link configuration";
        var result = Sut.Expand(input);
        result.Should().Contain(input);
        result.Should().Contain("MLAG peer-link configuration");
    }

    [Fact]
    public void StripsHowDoIPrefix()
    {
        var result = Sut.Expand("how do I configure MLAG peer-link");
        // The "configure " token then strips on top of "how do I "; final
        // noun phrase reaches "MLAG peer-link".
        result.Should().Contain("MLAG peer-link");
    }

    [Fact]
    public void StripsWhatIsPrefix()
    {
        var result = Sut.Expand("What is BGP EVPN type-5?");
        result.Should().Contain("BGP EVPN type-5?");
    }

    [Fact]
    public void NoVariantWhenQueryIsAlreadyMinimal()
    {
        var result = Sut.Expand("EVPN type-5");
        result.Should().HaveCount(1);
        result[0].Should().Be("EVPN type-5");
    }

    [Fact]
    public void DoesNotEmitDuplicatesWhenContractionMatchesOriginal()
    {
        // No parens to strip, no question prefix — should be a no-op.
        var result = Sut.Expand("static route configuration");
        result.Should().HaveCount(1);
    }

    [Fact]
    public void IgnoresPrefixWhenStrippingWouldLeaveTooFewChars()
    {
        // Stripping "what is " from "what is XX" leaves "XX" (2 chars) —
        // below the 3-char floor, so we keep the original.
        var result = Sut.Expand("what is OS");
        result.Should().HaveCount(1);
    }

    [Fact]
    public void NoopExpanderJustReturnsInput()
    {
        var noop = new NoopMultiQueryExpander();
        var result = noop.Expand("anything goes");
        result.Should().ContainSingle().Which.Should().Be("anything goes");
    }
}
