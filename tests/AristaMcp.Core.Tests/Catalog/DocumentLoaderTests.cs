using AristaMcp.Core.Catalog;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Core.Tests.Catalog;

public class DocumentLoaderTests
{
    [Fact]
    public void MarkdownWithCrlfLineEndings_StillSplitsHeadings()
    {
        // Regression canary: .NET regex `$` in Multiline mode doesn't match at `\r`,
        // only before `\n`. Windows-written MD files with CRLF line endings must still
        // have their ATX headings recognised. The loader normalises to LF before regex.
        const string md = "# First\r\n\r\nbody A\r\n\r\n## Second\r\n\r\nbody B\r\n";

        var sections = DocumentLoader.ExtractSectionsFromMarkdown(md, fallbackTitle: "doc");

        sections.Should().HaveCount(2);
        sections[0].Title.Should().Be("First");
        sections[0].Level.Should().Be((short)1);
        sections[1].Title.Should().Be("Second");
        sections[1].Level.Should().Be((short)2);
    }

    [Fact]
    public void MarkdownWithHeadings_SplitsIntoSections()
    {
        const string md = """
            # Introduction

            Overview body text here.

            ## Details

            Inner body here.

            # Next

            Second top-level body.
            """;

        var sections = DocumentLoader.ExtractSectionsFromMarkdown(md, fallbackTitle: "doc");

        sections.Should().HaveCount(3);
        sections[0].Title.Should().Be("Introduction");
        sections[0].Level.Should().Be((short)1);
        sections[0].Content.Should().StartWith("Overview body");
        sections[1].Title.Should().Be("Details");
        sections[1].Level.Should().Be((short)2);
        sections[2].Title.Should().Be("Next");
    }

    [Fact]
    public void MarkdownWithoutHeadings_UsesFallbackTitle_AsSingleSection()
    {
        const string md = "Just a blob of content with no headings whatsoever.";

        var sections = DocumentLoader.ExtractSectionsFromMarkdown(md, fallbackTitle: "Some Doc");

        sections.Should().HaveCount(1);
        sections[0].Title.Should().Be("Some Doc");
        sections[0].Level.Should().Be((short)1);
        sections[0].Content.Should().Be(md);
    }

    [Fact]
    public void PageMarkers_AreStripped()
    {
        const string md = """
            {0}---------

            # First

            Body one.

            {1}---------

            ## Sub

            Body two.
            """;

        var sections = DocumentLoader.ExtractSectionsFromMarkdown(md, fallbackTitle: "doc");

        sections.Should().HaveCount(2);
        sections[0].Content.Should().NotContain("{1}").And.NotContain("----");
        sections[1].Content.Should().NotContain("{");
    }

    [Fact]
    public void EmptySections_AreDropped()
    {
        const string md = """
            # A

            # B

            real body
            """;

        var sections = DocumentLoader.ExtractSectionsFromMarkdown(md, fallbackTitle: "doc");

        sections.Should().ContainSingle().Which.Title.Should().Be("B");
    }
}
