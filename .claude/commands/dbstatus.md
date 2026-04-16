---
description: Show arista-mcp database health — row counts, index sizes, last ingest.
---

Summarise the state of the local arista-mcp database.

**Run these queries** against `localhost:5434` as user `arista`, database `arista`:

```bash
psql -h localhost -p 5434 -U arista arista <<'SQL'
\echo '-- documents'
SELECT category, product, COUNT(*) FROM documents GROUP BY category, product ORDER BY 1, 2;
\echo '-- chunks'
SELECT COUNT(*) AS total, COUNT(embedding) AS with_embedding FROM chunks;
\echo '-- index sizes'
SELECT indexname, pg_size_pretty(pg_relation_size(indexrelid)) AS size
  FROM pg_stat_user_indexes
 WHERE schemaname = 'public' ORDER BY pg_relation_size(indexrelid) DESC;
\echo '-- last 3 ingest runs'
SELECT started_at, status, docs_upserted, chunks_upserted,
       extract(epoch from (finished_at - started_at))::int AS seconds, error_msg
  FROM ingest_runs ORDER BY started_at DESC LIMIT 3;
\echo '-- postgres version + extensions'
SELECT extname, extversion FROM pg_extension ORDER BY extname;
SQL
```

**Flag** anything abnormal:
- chunks with `embedding IS NULL` (ingest failure mid-way)
- `last ingest` status = `error` or `partial`
- HNSW index size >> embedding column size (bloat — suggest `REINDEX CONCURRENTLY`)
- missing extensions: expected `vector`, `vchord`, `vchord_bm25`, `pg_tokenizer`, `pg_trgm`
