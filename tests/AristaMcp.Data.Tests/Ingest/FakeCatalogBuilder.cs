using System.IO;
using System.Text.Json;

namespace AristaMcp.Data.Tests.Ingest;

// Writes a minimal arista-docs-shaped catalog to a temp directory:
//   tempDir/
//     catalog.json
//     converted/md/<slug>.md
//     converted/json/<slug>.json
public sealed class FakeCatalogBuilder : IDisposable
{
    public string RootDir { get; }
    public string CatalogPath => Path.Combine(RootDir, "catalog.json");

    private readonly List<object> _entries = [];

    public FakeCatalogBuilder()
    {
        RootDir = Path.Combine(Path.GetTempPath(), "arista-mcp-fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(RootDir, "md"));
        Directory.CreateDirectory(Path.Combine(RootDir, "json"));
    }

    public FakeCatalogBuilder AddDoc(
        string id,
        string slug,
        string title,
        string pdfSha256,
        string markdown)
    {
        var mdRel = $"md/{slug}.md";
        var jsonRel = $"json/{slug}.json";

        File.WriteAllText(Path.Combine(RootDir, mdRel), markdown);
        File.WriteAllText(
            Path.Combine(RootDir, jsonRel),
            JsonSerializer.Serialize(new { title, sections = Array.Empty<object>() }));

        _entries.Add(new
        {
            id,
            url = $"file:///{slug}",
            category = "toi",
            product = (string?)null,
            version = (string?)null,
            title,
            slug,
            tags = Array.Empty<string>(),
            pdf_path = (string?)null,
            md_path = mdRel,
            json_path = jsonRel,
            pdf_sha256 = pdfSha256,
            pages = 1,
            size_bytes = (long)markdown.Length,
            convert_mode = "accurate",
            image_count = 0,
            section_count = 1,
            level1_section_count = 1,
            toc_count = 0,
            downloaded_at = DateTimeOffset.UtcNow,
            converted_at = DateTimeOffset.UtcNow,
        });

        return this;
    }

    public string Build()
    {
        var catalog = new
        {
            generated_at = DateTimeOffset.UtcNow,
            documents = _entries.ToArray(),
        };

        var json = JsonSerializer.Serialize(catalog, FakeCatalogJson.Options);
        File.WriteAllText(CatalogPath, json);
        return CatalogPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(RootDir))
        {
            try
            {
                Directory.Delete(RootDir, recursive: true);
            }
            catch (IOException) { /* best-effort temp cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
        }
    }
}
