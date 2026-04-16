-- arista-mcp initial extensions + analyzer.
-- Runs once on the first container start, after POSTGRES_DB is created.
-- DB-agnostic: ALTER DATABASE statements target current_database() dynamically so the
-- same script works for the production `arista` DB and for the `arista_test` DB used
-- by Testcontainers.

CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS vchord CASCADE;
CREATE EXTENSION IF NOT EXISTS pg_tokenizer;
CREATE EXTENSION IF NOT EXISTS vchord_bm25 CASCADE;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

DO $$
DECLARE
    db text := current_database();
BEGIN
    EXECUTE format('ALTER DATABASE %I SET search_path TO "$user", public, tokenizer_catalog, bm25_catalog', db);
    EXECUTE format('ALTER DATABASE %I SET hnsw.iterative_scan = %L', db, 'relaxed_order');
    EXECUTE format('ALTER DATABASE %I SET hnsw.max_scan_tuples = 20000', db);
    EXECUTE format('ALTER DATABASE %I SET hnsw.ef_search = 100', db);
    EXECUTE format('ALTER DATABASE %I SET maintenance_work_mem = %L', db, '2GB');
    EXECUTE format('ALTER DATABASE %I SET jit = off', db);
END $$;

-- create_text_analyzer lives in tokenizer_catalog. Must be called qualified because
-- ALTER DATABASE SET search_path only affects future sessions, not this script.
SELECT tokenizer_catalog.create_text_analyzer('english_analyzer', $$
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
$$);
