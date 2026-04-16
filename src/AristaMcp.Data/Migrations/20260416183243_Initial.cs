using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace AristaMcp.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_tokenizer", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vchord", ",,")
                .Annotation("Npgsql:PostgresExtension:vchord_bm25", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    product = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    tags = table.Column<string>(type: "jsonb", nullable: false),
                    pages = table.Column<int>(type: "integer", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    pdf_sha256 = table.Column<string>(type: "text", nullable: true),
                    md_path = table.Column<string>(type: "text", nullable: false),
                    json_path = table.Column<string>(type: "text", nullable: false),
                    convert_mode = table.Column<string>(type: "text", nullable: true),
                    image_count = table.Column<int>(type: "integer", nullable: false),
                    section_count = table.Column<int>(type: "integer", nullable: false),
                    level1_section_count = table.Column<int>(type: "integer", nullable: false),
                    toc_count = table.Column<int>(type: "integer", nullable: false),
                    downloaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    converted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ingest_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    docs_total = table.Column<int>(type: "integer", nullable: false),
                    docs_skipped = table.Column<int>(type: "integer", nullable: false),
                    docs_upserted = table.Column<int>(type: "integer", nullable: false),
                    chunks_upserted = table.Column<int>(type: "integer", nullable: false),
                    catalog_sha256 = table.Column<string>(type: "text", nullable: true),
                    error_msg = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingest_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chunks",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    document_id = table.Column<string>(type: "text", nullable: false),
                    chunk_index = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    raw_content = table.Column<string>(type: "text", nullable: false),
                    section_title = table.Column<string>(type: "text", nullable: true),
                    section_level = table.Column<short>(type: "smallint", nullable: true),
                    page_start = table.Column<int>(type: "integer", nullable: true),
                    page_end = table.Column<int>(type: "integer", nullable: true),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    embedding = table.Column<HalfVector>(type: "halfvec(768)", nullable: false),
                    embedding_model = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunks", x => x.id);
                    table.ForeignKey(
                        name: "FK_chunks_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chunks_document_id",
                table: "chunks",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_chunks_document_id_chunk_index",
                table: "chunks",
                columns: new[] { "document_id", "chunk_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chunks_embedding",
                table: "chunks",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "halfvec_cosine_ops" })
                .Annotation("Npgsql:StorageParameter:ef_construction", 200)
                .Annotation("Npgsql:StorageParameter:m", 16);

            migrationBuilder.CreateIndex(
                name: "IX_chunks_section_level",
                table: "chunks",
                column: "section_level",
                filter: "section_level IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_documents_category_product_version",
                table: "documents",
                columns: new[] { "category", "product", "version" });

            migrationBuilder.CreateIndex(
                name: "idx_documents_pdf_sha256",
                table: "documents",
                column: "pdf_sha256");

            migrationBuilder.CreateIndex(
                name: "IX_ingest_runs_started_at",
                table: "ingest_runs",
                column: "started_at",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chunks");

            migrationBuilder.DropTable(
                name: "ingest_runs");

            migrationBuilder.DropTable(
                name: "documents");
        }
    }
}
