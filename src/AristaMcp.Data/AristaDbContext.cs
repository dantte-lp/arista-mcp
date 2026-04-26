using AristaMcp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AristaMcp.Data;

public class AristaDbContext(DbContextOptions<AristaDbContext> options) : DbContext(options)
{
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();
    public DbSet<ChunkEntity> Chunks => Set<ChunkEntity>();
    public DbSet<IngestRunEntity> IngestRuns => Set<IngestRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.HasPostgresExtension("vchord");
        modelBuilder.HasPostgresExtension("pg_tokenizer");
        modelBuilder.HasPostgresExtension("vchord_bm25");
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<DocumentEntity>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.IngestedAt).HasDefaultValueSql("now()");
            e.HasIndex(d => new { d.Category, d.Product, d.Version })
                .HasDatabaseName("idx_documents_category_product_version");
            e.HasIndex(d => d.PdfSha256).HasDatabaseName("idx_documents_pdf_sha256");
        });

        modelBuilder.Entity<ChunkEntity>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            e.Property(c => c.ChunkKind).HasDefaultValue("leaf");
            e.HasIndex(c => new { c.DocumentId, c.ChunkIndex }).IsUnique();
            e.HasIndex(c => c.DocumentId);
            e.HasIndex(c => c.SectionLevel).HasFilter("section_level IS NOT NULL");
            e.HasIndex(c => c.Embedding)
                .HasMethod("hnsw")
                .HasOperators("halfvec_cosine_ops")
                .HasStorageParameter("m", 16)
                .HasStorageParameter("ef_construction", 200);
            // Sprint 15: parent-child chunking.
            e.HasIndex(c => c.ParentChunkId)
                .HasDatabaseName("idx_chunks_parent_chunk_id")
                .HasFilter("parent_chunk_id IS NOT NULL");
            e.HasIndex(c => c.ChunkKind).HasDatabaseName("idx_chunks_kind");
            e.ToTable(t => t.HasCheckConstraint(
                "ck_chunks_chunk_kind",
                "chunk_kind IN ('leaf', 'parent')"));
            e.HasOne(c => c.Parent)
                .WithMany()
                .HasForeignKey(c => c.ParentChunkId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Document)
                .WithMany()
                .HasForeignKey(c => c.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IngestRunEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StartedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.StartedAt).IsDescending();
        });
    }
}
