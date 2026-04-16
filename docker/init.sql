-- arista-mcp initial extensions + analyzer.
-- Runs once on the first container start, after POSTGRES_DB is created.

CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS vchord CASCADE;
CREATE EXTENSION IF NOT EXISTS pg_tokenizer;
CREATE EXTENSION IF NOT EXISTS vchord_bm25 CASCADE;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

ALTER DATABASE arista SET search_path TO "$user", public, tokenizer_catalog, bm25_catalog;

-- create_text_analyzer lives in the tokenizer_catalog schema. Must be called qualified
-- because ALTER DATABASE ... search_path only affects future sessions, not this script.
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

-- HNSW tuning: iterative scan + higher recall. These GUCs are registered by the vector
-- extension; `vector` is added to shared_preload_libraries in compose.yaml so the GUCs
-- exist before ALTER DATABASE SET evaluates them.
ALTER DATABASE arista SET hnsw.iterative_scan = 'relaxed_order';
ALTER DATABASE arista SET hnsw.max_scan_tuples = 20000;
ALTER DATABASE arista SET hnsw.ef_search = 100;

ALTER DATABASE arista SET maintenance_work_mem = '2GB';
ALTER DATABASE arista SET jit = off;
