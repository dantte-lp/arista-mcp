using System.ComponentModel;
using System.Text.Json;
using AristaMcp.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace AristaMcp.Server.Tools;

[McpServerToolType]
public sealed class GetDocumentTool(AristaDbContext db)
{
    [McpServerTool(Name = "get_document")]
    [Description("Fetch full metadata and chunk count for a single document.")]
    public async Task<object> GetAsync(
        [Description("Document ID (from search_docs or list_documents).")] string documentId,
        CancellationToken ct = default)
    {
        var doc = await db.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            .ConfigureAwait(false);

        if (doc is null)
        {
            return new { found = false, document_id = documentId };
        }

        var chunkCount = await db.Chunks
            .AsNoTracking()
            .CountAsync(c => c.DocumentId == documentId, ct)
            .ConfigureAwait(false);

        var tags = JsonSerializer.Deserialize<string[]>(doc.TagsJson) ?? [];

        return new
        {
            found = true,
            id = doc.Id,
            url = doc.Url,
            title = doc.Title,
            slug = doc.Slug,
            category = doc.Category,
            product = doc.Product,
            version = doc.Version,
            tags,
            pages = doc.Pages,
            size_bytes = doc.SizeBytes,
            md_path = doc.MdPath,
            convert_mode = doc.ConvertMode,
            image_count = doc.ImageCount,
            section_count = doc.SectionCount,
            toc_count = doc.TocCount,
            chunk_count = chunkCount,
            downloaded_at = doc.DownloadedAt,
            converted_at = doc.ConvertedAt,
            ingested_at = doc.IngestedAt,
        };
    }
}
