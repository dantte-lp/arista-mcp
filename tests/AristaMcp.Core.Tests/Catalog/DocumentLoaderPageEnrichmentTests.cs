using AristaMcp.Core.Catalog;
using AristaMcp.Core.Chunking;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Core.Tests.Catalog;

public class DocumentLoaderPageEnrichmentTests
{
    [Fact]
    public void CleanHeading_StripsBoldItalicUnderscoreAndHtml()
    {
        DocumentLoader.CleanHeading("**Configuration**").Should().Be("Configuration");
        DocumentLoader.CleanHeading("*Introduction*").Should().Be("Introduction");
        DocumentLoader.CleanHeading("_Notes_").Should().Be("Notes");
        DocumentLoader.CleanHeading("Hello <span>world</span>").Should().Be("Hello world");
        DocumentLoader.CleanHeading("  spaced   out  ").Should().Be("spaced out");
    }

    [Fact]
    public void StampPages_MatchesByLevelAndCleanedTitle_InOrder()
    {
        var md = new List<Section>
        {
            new() { Title = "**Introduction**", Level = 1, Content = "body A" },
            new() { Title = "Details", Level = 2, Content = "body B" },
            new() { Title = "Next Top", Level = 1, Content = "body C" },
        };
        var json = new List<JsonSection>
        {
            new() { Title = "Introduction", Level = 1, PageStart = 0, PageEnd = 2 },
            new() { Title = "Details",      Level = 2, PageStart = 3, PageEnd = 5 },
            new() { Title = "Next Top",     Level = 1, PageStart = 6, PageEnd = 9 },
        };

        var stamped = DocumentLoader.StampPagesFromJson(md, json);

        stamped.Should().HaveCount(3);
        stamped[0].PageStart.Should().Be(0);
        stamped[0].PageEnd.Should().Be(2);
        stamped[1].PageStart.Should().Be(3);
        stamped[2].PageStart.Should().Be(6);
        stamped[2].PageEnd.Should().Be(9);
    }

    [Fact]
    public void StampPages_MdHeadingWithNoJsonCounterpart_GetsNullPages_SiblingsStillMatch()
    {
        // MD has an extra level-1 heading ("Oddball") that JSON doesn't know about.
        // That section stays null-pages; subsequent level-1 sections still match the
        // queued JSON entries in order.
        var md = new List<Section>
        {
            new() { Title = "Introduction", Level = 1, Content = "A" },
            new() { Title = "Oddball",      Level = 1, Content = "rogue" },
            new() { Title = "Conclusion",   Level = 1, Content = "C" },
        };
        var json = new List<JsonSection>
        {
            new() { Title = "Introduction", Level = 1, PageStart = 0, PageEnd = 1 },
            new() { Title = "Conclusion",   Level = 1, PageStart = 4, PageEnd = 6 },
        };

        var stamped = DocumentLoader.StampPagesFromJson(md, json);

        stamped[0].PageStart.Should().Be(0);
        stamped[1].PageStart.Should().BeNull();
        stamped[1].PageEnd.Should().BeNull();
        stamped[2].PageStart.Should().Be(4);
        stamped[2].PageEnd.Should().Be(6);
    }

    [Fact]
    public void StampPages_EmptyJsonSections_ReturnsMdUnchanged()
    {
        var md = new List<Section>
        {
            new() { Title = "A", Level = 1, Content = "body" },
        };

        var stamped = DocumentLoader.StampPagesFromJson(md, []);

        stamped.Should().BeEquivalentTo(md);
    }

    [Fact]
    public void StampPages_LevelMismatch_StillMatchesByTitleOrder()
    {
        // arista-docs' enrich.py flattens the TOC — every JSON section is emitted as
        // level=1 regardless of the MD's actual `#`/`##`/`###` depth. Pairing must be
        // title-order-based, not level-keyed, otherwise 28%+ of chunks get null pages.
        var md = new List<Section>
        {
            new() { Title = "Overview",   Level = 3, Content = "first" },
            new() { Title = "Deep Dive",  Level = 4, Content = "second" },
        };
        var json = new List<JsonSection>
        {
            new() { Title = "Overview",  Level = 1, PageStart = 0, PageEnd = 1 },
            new() { Title = "Deep Dive", Level = 1, PageStart = 2, PageEnd = 3 },
        };

        var stamped = DocumentLoader.StampPagesFromJson(md, json);

        stamped[0].PageStart.Should().Be(0);
        stamped[1].PageStart.Should().Be(2);
    }

    [Fact]
    public void StampPages_Lookahead_SkipsOneUnmatchedMdHeadingThenResumes()
    {
        // An extra MD heading absent from JSON shouldn't derail the rest of the doc.
        var md = new List<Section>
        {
            new() { Title = "Introduction", Level = 1, Content = "A" },
            new() { Title = "Unknown",      Level = 1, Content = "rogue" },
            new() { Title = "Conclusion",   Level = 1, Content = "C" },
        };
        var json = new List<JsonSection>
        {
            new() { Title = "Introduction", Level = 1, PageStart = 0, PageEnd = 1 },
            new() { Title = "Conclusion",   Level = 1, PageStart = 4, PageEnd = 6 },
        };

        var stamped = DocumentLoader.StampPagesFromJson(md, json);

        stamped[0].PageStart.Should().Be(0);
        stamped[1].PageStart.Should().BeNull("'Unknown' is absent from JSON sections");
        stamped[2].PageStart.Should().Be(4, "lookahead re-syncs Conclusion after the gap");
    }
}
