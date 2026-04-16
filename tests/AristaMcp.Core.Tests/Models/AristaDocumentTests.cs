using AristaMcp.Core.Models;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Core.Tests.Models;

public class AristaDocumentTests
{
    [Fact]
    public void AristaDocument_DefaultTags_IsEmptyList()
    {
        var doc = new AristaDocument
        {
            Id = "x",
            Url = "u",
            Category = "toi",
            Title = "t",
            Slug = "s",
            MdPath = "a",
            JsonPath = "b",
        };

        doc.Tags.Should().BeEmpty();
    }

    [Fact]
    public void AristaDocument_Equality_IsByValue()
    {
        var a = new AristaDocument
        {
            Id = "x",
            Url = "u",
            Category = "toi",
            Title = "t",
            Slug = "s",
            MdPath = "a",
            JsonPath = "b",
        };
        var b = a with { Title = "t" };

        a.Should().Be(b);
    }
}
