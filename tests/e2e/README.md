# tests/e2e/

Shell-based end-to-end scenarios that go beyond what xUnit can reasonably express
(full schema provisioning, real-catalog ingest). C# E2E tests live under
`tests/AristaMcp.E2E/` and cover transport-level smokes.

## Scenarios

| Script | Coverage |
|---|---|
| `01-fresh-deploy.sh` | `compose down -v` → `compose up` → extensions → fetch-models → `dotnet ef database update` → schema verification (bm25v + HNSW + BM25 + triggers) |
| `02-category-ingest.sh` | ingest `--category manual` (225 docs) → row counts + `bm25v IS NULL == 0` → re-ingest without `--force` returns `status=skipped` |
| `03-post-reconvert-smoke.sh` | post-`purge-fakes` + real-Marker reconversion gate: zero placeholder conversions, fresh full-corpus ingest, bench top-10 ≥ 90 % / p95 ≤ 1.2 s, curate-triples ≥ 500 rows. Promotion gate for `v0.1.4-rc*` → `v0.1.4`. |

## Running

From repo root:

    bash tests/e2e/01-fresh-deploy.sh
    bash tests/e2e/02-category-ingest.sh

`01` is fully idempotent (blows away the podman volume). `02` assumes `01`
already ran or the DB is at least schema-green.

### Environment overrides

| Var | Default | Purpose |
|-----|---------|---------|
| `PGHOST` / `PGPORT` / `PGUSER` / `PGDATABASE` / `PGPASSWORD` | local compose values | standard libpq env |
| `ARISTA_PG_CONTAINER` | `arista-mcp-postgres` | container name for `podman exec` |
| `ARISTA_DOCS_CATALOG` | `../arista-docs/data/catalog.json` | override the catalog path |
| `ARISTA_BENCH_QUERIES` | `tests/fixtures/bench-queries.json` | bench/triples query set (03) |
| `ARISTA_BENCH_HISTORY` | `tests/fixtures/bench-history.jsonl` | bench JSONL history (03) |
| `ARISTA_BENCH_LABEL` | `v0.1.4-full-corpus-crlf` | tag for the bench row (03) |
| `ARISTA_MIN_TOP10` | `90` | top-10 hit-rate gate in % (03) |
| `ARISTA_MAX_P95_MS` | `1200` | p95 latency gate in ms (03) |
| `ARISTA_MIN_TRIPLES` | `500` | triple count gate (03) |
| `ARISTA_MIN_CHUNKS` | `25000` | minimum post-ingest chunk count (03) |

### WSL2 / Windows Podman

If `localhost:5434` isn't reachable from Windows host, either enable
`podman machine set --user-mode-networking` or set `PGHOST` to the WSL IP
(`wsl -d podman-machine-default -- ip -4 addr show eth0`). The scripts use
`podman exec` for `psql` so they bypass the host networking issue entirely.

## Philosophy

Scripts are `set -euo pipefail`, colour-coded, fail fast on first mismatch, print
the assertion outcome inline. Any C#-expressible coverage belongs in
`tests/AristaMcp.E2E/` — this folder is reserved for scenarios that need shell
orchestration (compose control, EF CLI, psql metaqueries).
