---
description: Run arista-mcp ingest against the local postgres container, streaming progress.
---

Ingest the arista-docs catalog into the local pgvector database.

**Prereqs** (check and fix if missing before running):
1. `pg_isready -h localhost -p 5434 -U arista -d arista` returns success.
   If not: `podman-compose -f docker/compose.yaml up -d postgres` and wait for healthcheck.
2. arista-docs catalog at `../arista-docs/data/catalog.json` exists.

**Run:**

```bash
dotnet run --project src/AristaMcp.Cli -- ingest --verbose
```

If `$ARGUMENTS` is non-empty, append it as additional flags (e.g. `/ingest --dry-run`, `/ingest --force`, `/ingest --category toi`).

**After it finishes**, summarise:
- rows in `ingest_runs` (last row: status, counts, duration)
- via `psql -h localhost -p 5434 -U arista arista -c "SELECT * FROM ingest_runs ORDER BY started_at DESC LIMIT 1"`
