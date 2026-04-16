-- Manual migration 001: bm25v column + BM25 index on chunks.
--
-- pg_tokenizer.rs exposes tokenize() as STABLE, so it cannot be used directly in a
-- STORED GENERATED column (Postgres requires IMMUTABLE). Instead we rely on the
-- helper create_custom_model_tokenizer_and_trigger which provisions:
--   1. a custom tokenizer backed by an existing text analyzer,
--   2. a BM25 model (required for scoring at query time),
--   3. an AFTER INSERT/UPDATE trigger on (table, source_column) that writes the
--      tokenized bm25vector into target_column.
--
-- The index is then built against the populated column. Queries use
--   bm25query('idx_chunks_bm25', tokenize(@q, 'chunks_tokenizer'))
-- which ensures the same tokenization at write and read time.

ALTER TABLE chunks ADD COLUMN IF NOT EXISTS bm25v bm25vector;

SELECT tokenizer_catalog.create_custom_model_tokenizer_and_trigger(
    tokenizer_name     => 'chunks_tokenizer',
    model_name         => 'chunks_model',
    text_analyzer_name => 'english_analyzer',
    table_name         => 'chunks',
    source_column      => 'content',
    target_column      => 'bm25v'
);

CREATE INDEX IF NOT EXISTS idx_chunks_bm25 ON chunks USING bm25 (bm25v bm25_ops);
