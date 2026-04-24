# Getting started

Bring the stack up on a developer laptop in about 15 minutes.

## Requirements

- **.NET SDK 10.0.201+** (pinned in `global.json`).
- **Podman** with `podman compose`, or Docker Compose v2. WSL2 or native
  Linux host works; macOS untested.
- **PowerShell 7+** — `scripts/fetch-models.ps1` runs cross-platform.
- **~5 GB free disk** — ONNX models (~530 MB), PostgreSQL data (1–3 GB
  for the full catalog), build output.
- Optional: **CUDA 12+** if you want the `-p:UseGpuOnnx=true` build for
  GPU-accelerated ingest.
- Optional: **`arista-docs` catalog** checked out alongside this repo —
  needed to run `ingest` against real content. The schema works fine
  empty for smoke tests.

## 1 — PostgreSQL with vector + BM25 extensions

```bash
podman compose -f docker/compose.yaml up -d postgres
```

The image is `tensorchord/vchord-suite:pg18-latest`, pre-loaded with
pgvector, vchord, vchord_bm25 and pg_tokenizer. The compose file wires
`shared_preload_libraries` correctly.

Verify:

```bash
podman exec arista-mcp-postgres psql -U arista -d arista \
  -c "SELECT extversion FROM pg_extension WHERE extname='vector';"
```

### Windows / Podman note

Default Podman on WSL2 binds published ports at `127.0.0.1` inside the
WSL VM, unreachable from Windows. One-time fix:

```powershell
podman machine stop
podman machine set --user-mode-networking
podman machine start
```

Alternatively, `docker/compose.yaml` already publishes PostgreSQL on
`0.0.0.0:5434` inside the WSL distro so the `vEthernet (WSL)` adapter
can reach it.

## 2 — Fetch ONNX models

```bash
pwsh scripts/fetch-models.ps1
```

Downloads into `models/`:

- `embedder/model.onnx` + `vocab.txt` — `snowflake-arctic-embed-m-v1.5`
  (~436 MB).
- `reranker/model.onnx` + `vocab.txt` — `ms-marco-MiniLM-L6-v2` (~91 MB).
- `llm/qwen2.5-1.5b-instruct-q4_k_m.gguf` — Qwen2.5-1.5B Q4_K_M for
  optional HyDE (~1 GB).

The script is idempotent — re-running skips files that already match
the minimum-size check.

## 3 — Schema

```bash
dotnet ef database update \
  --project src/AristaMcp.Data \
  --startup-project src/AristaMcp.Data
```

This provisions `documents`, `chunks` (with `halfvec(768)` embedding +
`bm25v bm25vector` + the BM25 trigger), `ingest_runs`, the HNSW
`halfvec_cosine_ops` index and the `idx_chunks_bm25` index.

## 4 — Ingest

Point `arista-mcp` at an `arista-docs` catalog JSON and let it chunk +
embed + upsert:

```bash
# Full catalog — ~25 min on a 12-core CPU
dotnet run --project src/AristaMcp.Cli -- ingest \
  --catalog ../arista-docs/catalog.json

# Or one category while iterating
dotnet run --project src/AristaMcp.Cli -- ingest \
  --catalog ../arista-docs/catalog.json \
  --category avd
```

Flags:

- `--force` — ignore incremental-skip SHA256 checks and re-ingest.
- `--dry-run` — print what would be ingested without writing.
- `--verbose` — chunk-level progress.

## 5 — Run the MCP server

### Stdio (Claude Desktop / Claude Code)

```bash
dotnet run --project src/AristaMcp.Cli -- serve --transport stdio
```

Add to Claude Desktop's `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "arista": {
      "command": "dotnet",
      "args": [
        "run", "--project", "/absolute/path/to/arista-mcp/src/AristaMcp.Cli",
        "--no-build", "--",
        "serve", "--transport", "stdio"
      ]
    }
  }
}
```

Stdio emits **logs on stderr only** — stdout is the MCP transport.

### HTTP (curl / raw clients)

```bash
dotnet run --project src/AristaMcp.Cli -- serve --transport http --port 8080
```

Streamable HTTP at `http://127.0.0.1:8080/`, stateless.

```bash
curl -X POST http://127.0.0.1:8080/mcp \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

More recipes in [`../mcp-integration.md`](../mcp-integration.md).

## 6 — Smoke-test retrieval

```bash
dotnet run --project src/AristaMcp.Cli -- bench \
  --queries tests/fixtures/bench-queries-v2.json \
  --limit 10 \
  --history tests/fixtures/bench-history.jsonl \
  --label my-first-bench
```

Expect top-1 around **90.82 %** on the v2 set against the stock MiniLM
reranker. Full methodology in [benchmarking.md](benchmarking.md).

## Configuration reference

Every `AristaMcpSettings` property can be overridden by environment
variable (prefix `ARISTA_MCP__`, use `__` for nesting) or by an
`arista-mcp.json` file in the working directory. Common overrides:

| Env var                                 | Default                                                            |
|-----------------------------------------|--------------------------------------------------------------------|
| `ARISTA_MCP__ConnectionString`          | `Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista` |
| `ARISTA_MCP__ModelsDir`                 | `models`                                                            |
| `ARISTA_MCP__EmbeddingVariant`          | `fp32` (swap to `fp16` for ~1.5–2× CPU speedup at ≤1 pp nDCG cost)  |
| `ARISTA_MCP__Gpu`                       | `false`                                                             |
| `ARISTA_MCP__HttpPort`                  | `8080`                                                              |
| `ARISTA_MCP__Hyde__Enabled`             | `false` — set `true` to switch on HyDE via the llm sidecar          |
| `ARISTA_MCP__Hyde__Endpoint`            | `http://127.0.0.1:8090/v1/chat/completions`                         |
| `ARISTA_MCP__Otel__Endpoint`            | unset — set to `http://localhost:4317` to ship OTLP spans to Jaeger |

## Test / production database isolation

The integration + E2E suites use a separate `arista_test` database.
`PgvectorFixture` creates it on first use and **refuses to run against
any database whose name doesn't end in `_test`** — the guard saved us
from a "release-gate wipes prod" incident during Sprint 5 and is
deliberately strict.

```bash
podman exec arista-mcp-postgres psql -U arista -d arista      -c "SELECT COUNT(*) FROM chunks;"
podman exec arista-mcp-postgres psql -U arista -d arista_test -c "SELECT COUNT(*) FROM chunks;"
```

## Next

- [architecture.md](architecture.md) — how the projects and runtime fit.
- [retrieval.md](retrieval.md) — how a query becomes a result.
- [mcp-tools.md](mcp-tools.md) — per-tool schemas and example payloads.
- [development.md](development.md) — build, test, contribute.
