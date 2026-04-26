using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace AristaMcp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddParentChunkSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "embedding_model",
                table: "chunks",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<HalfVector>(
                name: "embedding",
                table: "chunks",
                type: "halfvec(768)",
                nullable: true,
                oldClrType: typeof(HalfVector),
                oldType: "halfvec(768)");

            migrationBuilder.AddColumn<string>(
                name: "chunk_kind",
                table: "chunks",
                type: "text",
                nullable: false,
                defaultValue: "leaf");

            migrationBuilder.AddColumn<long>(
                name: "parent_chunk_id",
                table: "chunks",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_chunks_kind",
                table: "chunks",
                column: "chunk_kind");

            migrationBuilder.CreateIndex(
                name: "idx_chunks_parent_chunk_id",
                table: "chunks",
                column: "parent_chunk_id",
                filter: "parent_chunk_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chunks_chunk_kind",
                table: "chunks",
                sql: "chunk_kind IN ('leaf', 'parent')");

            migrationBuilder.AddForeignKey(
                name: "FK_chunks_chunks_parent_chunk_id",
                table: "chunks",
                column: "parent_chunk_id",
                principalTable: "chunks",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chunks_chunks_parent_chunk_id",
                table: "chunks");

            migrationBuilder.DropIndex(
                name: "idx_chunks_kind",
                table: "chunks");

            migrationBuilder.DropIndex(
                name: "idx_chunks_parent_chunk_id",
                table: "chunks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chunks_chunk_kind",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "chunk_kind",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "parent_chunk_id",
                table: "chunks");

            migrationBuilder.AlterColumn<string>(
                name: "embedding_model",
                table: "chunks",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<HalfVector>(
                name: "embedding",
                table: "chunks",
                type: "halfvec(768)",
                nullable: false,
                oldClrType: typeof(HalfVector),
                oldType: "halfvec(768)",
                oldNullable: true);
        }
    }
}
