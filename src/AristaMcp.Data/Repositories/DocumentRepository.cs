using System.Text.Json;
using AristaMcp.Core.Models;
using AristaMcp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AristaMcp.Data.Repositories;

public class DocumentRepository(AristaDbContext db, TimeProvider clock) : IDocumentRepository
{
    public async Task UpsertAsync(AristaDocument doc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var entity = await db.Documents.FirstOrDefaultAsync(x => x.Id == doc.Id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new DocumentEntity { Id = doc.Id };
            db.Documents.Add(entity);
        }

        entity.Url = doc.Url;
        entity.Category = doc.Category;
        entity.Product = doc.Product;
        entity.Version = doc.Version;
        entity.Title = doc.Title;
        entity.Slug = doc.Slug;
        entity.TagsJson = JsonSerializer.Serialize(doc.Tags);
        entity.Pages = doc.Pages;
        entity.SizeBytes = doc.SizeBytes;
        entity.PdfSha256 = doc.PdfSha256;
        entity.MdPath = doc.MdPath;
        entity.JsonPath = doc.JsonPath;
        entity.ConvertMode = doc.ConvertMode;
        entity.ImageCount = doc.ImageCount;
        entity.SectionCount = doc.SectionCount;
        entity.Level1SectionCount = doc.Level1SectionCount;
        entity.TocCount = doc.TocCount;
        entity.DownloadedAt = doc.DownloadedAt;
        entity.ConvertedAt = doc.ConvertedAt;
        entity.IngestedAt = clock.GetUtcNow();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<AristaDocument?> GetByIdAsync(string id, CancellationToken ct)
    {
        var e = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        return e is null ? null : MapToDomain(e);
    }

    public async Task<IReadOnlyList<string>> GetAllIdsAsync(CancellationToken ct) =>
        await db.Documents.AsNoTracking().Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false);

    public Task<string?> GetPdfSha256Async(string id, CancellationToken ct) =>
        db.Documents.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => x.PdfSha256)
            .FirstOrDefaultAsync(ct);

    public Task DeleteAsync(string id, CancellationToken ct) =>
        db.Documents.Where(x => x.Id == id).ExecuteDeleteAsync(ct);

    private static AristaDocument MapToDomain(DocumentEntity e) => new()
    {
        Id = e.Id,
        Url = e.Url,
        Category = e.Category,
        Product = e.Product,
        Version = e.Version,
        Title = e.Title,
        Slug = e.Slug,
        Tags = JsonSerializer.Deserialize<List<string>>(e.TagsJson) ?? [],
        Pages = e.Pages,
        SizeBytes = e.SizeBytes,
        PdfSha256 = e.PdfSha256,
        MdPath = e.MdPath,
        JsonPath = e.JsonPath,
        ConvertMode = e.ConvertMode,
        ImageCount = e.ImageCount,
        SectionCount = e.SectionCount,
        Level1SectionCount = e.Level1SectionCount,
        TocCount = e.TocCount,
        DownloadedAt = e.DownloadedAt,
        ConvertedAt = e.ConvertedAt,
    };
}
