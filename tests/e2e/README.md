# tests/e2e/

Shell-based end-to-end scenarios that go beyond what xUnit can reasonably express
(full schema provisioning, real-catalog ingest). C# E2E tests live under
`tests/AristaMcp.E2E/` and cover transport-level smokes.

## Scenarios

| Script | Coverage |
|---|---|
| `01-fresh-deploy.sh` | `compose down -v` → `compose up` → extensions → fetch-models → `dotnet ef database update` → schema verification (bm25v + HNSW + BM25 + triggers) |
| `02-category-ingest.sh` | ingest `--category manual` (225 docs) → row counts + `bm25v IS NULL == 0` → re-ingest without `--force` returns `status=skipped` |

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
