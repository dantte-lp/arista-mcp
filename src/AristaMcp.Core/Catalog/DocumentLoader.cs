using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AristaMcp.Core.Chunking;
using AristaMcp.Core.Models;

namespace AristaMcp.Core.Catalog;

// Loads an arista-docs catalog entry into the domain model. MD file supplies section
// bodies (split on ATX headings); per-doc {slug}.json supplies page spans per section.
// Marker chunk markers ({N}-----) are stripped before sectioning. Heading titles are
// matched between MD and JSON via the same cleaner arista-docs.enrich._clean_heading
// applies on its side — strip markdown emphasis + inline HTML, collapse whitespace.
public static partial class DocumentLoader
{
    [GeneratedRegex(@"\{\d+\}-+", RegexOptions.Multiline | RegexOptions.ExplicitCapture)]
    private static partial Regex PageMarkerRegex();

    [GeneratedRegex(
        @"^(?<level>\#{1,6})[ \t]+(?<title>[^\r\n]+)$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture)]
    private static partial Regex HeadingRegex();

    // Mirrors enrich._clean_heading on the Python side:
    //   strip **bold**, *italic*, _underscore_, any <tag>…</tag>, trim + collapse WS.
    [GeneratedRegex(@"<[^>]+>", RegexOptions.ExplicitCapture)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\*\*(?<inner>[^*]+)\*\*", RegexOptions.ExplicitCapture)]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<!\*)\*(?<inner>[^*]+)\*(?!\*)", RegexOptions.ExplicitCapture)]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"(?<!_)_(?<inner>[^_]+)_(?!_)", RegexOptions.ExplicitCapture)]
    private static partial Regex UnderscoreRegex();

    [GeneratedRegex(@"\s+", RegexOptions.ExplicitCapture)]
    private static partial Regex WhitespaceRegex();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<LoadedDocument> LoadAsync(
        CatalogEntry entry,
        string catalogBaseDir,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(catalogBaseDir);

        var mdFull = Path.Combine(catalogBaseDir, NormalizePath(entry.MdPath));
        if (!File.Exists(mdFull))
        {
            throw new FileNotFoundException($"MD file missing for doc {entry.Id}", mdFull);
        }

        var md = await File.ReadAllTextAsync(mdFull, ct).ConfigureAwait(false);

        var jsonFull = Path.Combine(catalogBaseDir, NormalizePath(entry.JsonPath));
        var enriched = await TryLoadEnrichedJsonAsync(jsonFull, ct).ConfigureAwait(false);

        var mdSections = ExtractSectionsFromMarkdown(md, fallbackTitle: entry.Title);
        var sections = enriched is null
            ? mdSections
            : StampPagesFromJson(mdSections, enriched.Sections);

        var metadata = new AristaDocument
        {
            Id = entry.Id,
            Url = entry.Url,
            Category = entry.Category,
            Product = entry.Product,
            Version = entry.Version,
            Title = entry.Title,
            Slug = entry.Slug,
            Tags = entry.Tags,
            Pages = entry.Pages,
            SizeBytes = entry.SizeBytes,
            PdfSha256 = entry.PdfSha256,
            MdPath = entry.MdPath,
            JsonPath = entry.JsonPath,
            ConvertMode = entry.ConvertMode,
            ImageCount = entry.ImageCount,
            SectionCount = entry.SectionCount,
            Level1SectionCount = entry.Level1SectionCount,
            TocCount = entry.TocCount,
            DownloadedAt = entry.DownloadedAt,
            ConvertedAt = entry.ConvertedAt,
        };

        return new LoadedDocument
        {
            Metadata = metadata,
            Sections = sections,
        };
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    public static IReadOnlyList<Section> ExtractSectionsFromMarkdown(string md, string fallbackTitle)
    {
        // Normalise CRLF → LF first. .NET regex `$` in Multiline mode matches only
        // before `\n`, never at `\r`, so on Windows-written MD files the heading regex
        // `[^\r\n]+$` fails for every heading and the whole doc collapses to one
        // fallback-titled section. Prior Sprint 2 unit tests used LF raw-string literals
        // and never caught this.
        var lf = md.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace("\r", "\n", StringComparison.Ordinal);

        // Strip marker chunk markers so they don't bleed into section bodies.
        var cleaned = PageMarkerRegex().Replace(lf, string.Empty);

        var matches = HeadingRegex().Matches(cleaned);
        if (matches.Count == 0)
        {
            var body = cleaned.Trim();
            return body.Length == 0
                ? []
                : [new Section { Title = fallbackTitle, Level = 1, Content = body }];
        }

        var sections = new List<Section>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var level = (short)m.Groups["level"].Value.Length;
            var title = m.Groups["title"].Value.Trim();

            var bodyStart = m.Index + m.Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : cleaned.Length;
            var body = cleaned[bodyStart..bodyEnd].Trim();

            if (body.Length == 0)
            {
                continue;
            }

            sections.Add(new Section { Title = title, Level = level, Content = body });
        }

        if (sections.Count == 0)
        {
            var body = cleaned.Trim();
            return body.Length == 0
                ? []
                : [new Section { Title = fallbackTitle, Level = 1, Content = body }];
        }

        return sections;
    }

    // Same normalisation arista-docs/enrich.py applies on its side. Stripping emphasis
    // + HTML makes MD "**Configuration**" match JSON "Configuration".
    public static string CleanHeading(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var s = raw;
        s = HtmlTagRegex().Replace(s, string.Empty);
        s = BoldRegex().Replace(s, "${inner}");
        s = ItalicRegex().Replace(s, "${inner}");
        s = UnderscoreRegex().Replace(s, "${inner}");
        s = WhitespaceRegex().Replace(s, " ").Trim();
        return s;
    }

    // Pairs MD-derived sections to JSON-derived sections in order by cleaned_title.
    // LEVEL IS IGNORED: arista-docs' enrich.build_sections emits level-1 entries for
    // the entire flattened TOC, while the MD walker reads real `#`/`##`/`###` depth
    // (MD L3 ↔ JSON L1 is common). The sequence of cleaned titles is the authoritative
    // pairing.
    //
    // Forward scan with a small lookahead: MD heading at position i matches the next
    // unconsumed JSON entry whose cleaned title equals this MD's cleaned title within
    // a short window. If no match inside the window, that MD chunk stays null-paged
    // and the JSON cursor doesn't advance — the next MD heading may still pair with
    // the skipped JSON entry. Resilient to a few extra MD or JSON headings without
    // losing alignment for the rest of the doc.
    public static IReadOnlyList<Section> StampPagesFromJson(
        IReadOnlyList<Section> mdSections,
        IReadOnlyList<JsonSection> jsonSections)
    {
        ArgumentNullException.ThrowIfNull(mdSections);
        ArgumentNullException.ThrowIfNull(jsonSections);

        if (mdSections.Count == 0 || jsonSections.Count == 0)
        {
            return mdSections;
        }

        var jsonCleaned = new (string Cleaned, JsonSection Section)[jsonSections.Count];
        for (var i = 0; i < jsonSections.Count; i++)
        {
            jsonCleaned[i] = (CleanHeading(jsonSections[i].Title), jsonSections[i]);
        }

        const int Lookahead = 3;
        var result = new List<Section>(mdSections.Count);
        var jsonIdx = 0;

        foreach (var md in mdSections)
        {
            var mdClean = CleanHeading(md.Title);
            var matchAt = -1;
            var bound = Math.Min(jsonCleaned.Length, jsonIdx + Lookahead + 1);
            for (var probe = jsonIdx; probe < bound; probe++)
            {
                if (string.Equals(jsonCleaned[probe].Cleaned, mdClean, StringComparison.Ordinal))
                {
                    matchAt = probe;
                    break;
                }
            }

            if (matchAt >= 0)
            {
                var js = jsonCleaned[matchAt].Section;
                result.Add(md with { PageStart = js.PageStart, PageEnd = js.PageEnd });
                jsonIdx = matchAt + 1;
            }
            else
            {
                // Extra MD heading the JSON side doesn't know about. Leave null-paged
                // and try to re-sync on the next MD heading against the same cursor.
                result.Add(md);
            }
        }

        return result;
    }

    private static async Task<EnrichedDocumentJson?> TryLoadEnrichedJsonAsync(
        string jsonFull,
        CancellationToken ct)
    {
        if (!File.Exists(jsonFull))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(jsonFull);
            return await JsonSerializer.DeserializeAsync<EnrichedDocumentJson>(
                stream, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Corrupt / partial JSON — fall back to MD-only behaviour rather than abort.
            return null;
        }
    }
}
