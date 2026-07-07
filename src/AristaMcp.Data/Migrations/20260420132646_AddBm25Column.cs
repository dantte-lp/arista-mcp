using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AristaMcp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBm25Column : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // pg_tokenizer.tokenize() is STABLE (not IMMUTABLE) so a GENERATED STORED column
            // is rejected. tokenizer_catalog.create_custom_model_tokenizer_and_trigger
            // provisions (tokenizer, BM25 model, BEFORE INSERT/UPDATE trigger) that writes
            // the bm25vector into target_column. Queries: bm25v <&> to_bm25query(idx,
            // tokenize(q, 'chunks_tokenizer')::bm25vector).
            migrationBuilder.Sql("ALTER TABLE chunks ADD COLUMN IF NOT EXISTS bm25v bm25vector;");

            // Self-contained: `create_custom_model_tokenizer_and_trigger` below references
            // `english_analyzer`, which is otherwise created only by docker/init.sql. A plain
            // `dotnet ef database update` against a fresh DB would fail with "TextAnalyzer not found",
            // so create it here idempotently (config verbatim from docker/init.sql).
            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM tokenizer_catalog.text_analyzer WHERE name = 'english_analyzer') THEN
                        PERFORM tokenizer_catalog.create_text_analyzer('english_analyzer', $cfg$
                            pre_tokenizer = "unicode_segmentation"
                            [[character_filters]]
                            to_lowercase = {}
                            [[character_filters]]
                            unicode_normalization = "nfkd"
                            [[token_filters]]
                            skip_non_alphanumeric = {}
                            [[token_filters]]
                            stopwords = "nltk_english"
                            [[token_filters]]
                            stemmer = "english_porter2"
                        $cfg$);
                    END IF;
                END $do$;
                """);

            migrationBuilder.Sql("""
                SELECT tokenizer_catalog.create_custom_model_tokenizer_and_trigger(
                    tokenizer_name     => 'chunks_tokenizer',
                    model_name         => 'chunks_model',
                    text_analyzer_name => 'english_analyzer',
                    table_name         => 'chunks',
                    source_column      => 'content',
                    target_column      => 'bm25v'
                );
                """);

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS idx_chunks_bm25 ON chunks USING bm25 (bm25v bm25_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_chunks_bm25;");

            // The trigger/model helpers don't expose a single drop — model + trigger are
            // cleaned up implicitly when the column is dropped, which cascades the TRIGGER.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS model_chunks_model_trigger ON chunks;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS model_chunks_model_trigger_insert ON chunks;");
            migrationBuilder.Sql("DELETE FROM tokenizer_catalog.model WHERE name = 'chunks_model';");
            migrationBuilder.Sql("SELECT tokenizer_catalog.drop_tokenizer('chunks_tokenizer');");
            migrationBuilder.Sql("ALTER TABLE chunks DROP COLUMN IF EXISTS bm25v;");
        }
    }
}
