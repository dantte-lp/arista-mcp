using System.ComponentModel;
using AristaMcp.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace AristaMcp.Server.Tools;

[McpServerToolType]
public sealed class ListDocumentsTool(AristaDbContext db)
{
    [McpServerTool(Name = "list_documents")]
    [Description("List documents in the store, filtered by category and/or product.")]
    public async Task<object> ListAsync(
        [Description("Optional category filter ('toi' or 'manual').")] string? category = null,
        [Description("Optional product filter.")] string? product = null,
        [Description("Maximum rows returned. Defaults to 100.")] int limit = 100,
        CancellationToken ct = default)
    {
        var capped = Math.Clamp(limit, 1, 500);

        var q = db.Documents.AsNoTracking();
        if (category is not null)
        {
            q = q.Where(d => d.Category == category);
        }

        if (product is not null)
        {
            q = q.Where(d => d.Product == product);
        }

        var rows = await q
            .OrderBy(d => d.Category)
            .ThenBy(d => d.Title)
            .Take(capped)
            .Select(d => new
            {
                id = d.Id,
                title = d.Title,
                category = d.Category,
                product = d.Product,
                version = d.Version,
                slug = d.Slug,
                pages = d.Pages,
                section_count = d.SectionCount,
                ingested_at = d.IngestedAt,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new { count = rows.Count, documents = rows };
    }
}
