using System.IO;
using System.Text.RegularExpressions;
using AristaMcp.Core.Chunking;
using AristaMcp.Core.Models;

namespace AristaMcp.Core.Catalog;

// Loads an arista-docs catalog entry into the domain model. The per-doc JSON gives us
// the canonical title (post-enrichment); section bodies are extracted from the MD file
// by splitting on ATX headings (# / ## / ###). Marker chunk markers (`{N}-----`) are
// stripped before sectioning.
public static partial class DocumentLoader
{
    [GeneratedRegex(@"\{\d+\}-+", RegexOptions.Multiline | RegexOptions.ExplicitCapture)]
    private static partial Regex PageMarkerRegex();

    [GeneratedRegex(
        @"^(?<level>\#{1,6})[ \t]+(?<title>[^\r\n]+)$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture)]
    private static partial Regex HeadingRegex();

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
        var sections = ExtractSectionsFromMarkdown(md, fallbackTitle: entry.Title);

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
        // Strip marker chunk markers so they don't bleed into section bodies.
        var cleaned = PageMarkerRegex().Replace(md, string.Empty);

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
}
