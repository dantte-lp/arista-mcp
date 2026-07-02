# Getting started

Three install paths, ranked by friction:

1. **One-command install** from a release tarball (`bash scripts/install.sh`)
   — recommended for anyone who just wants search to work.
2. **Manual bootstrap** with the pre-built binary
   (`arista-mcp bootstrap --release …`) — for operators who want to see
   each step.
3. **Source build** (`dotnet publish` + `dotnet run`) — for contributors.

## Fastest path — `scripts/install.sh`

```bash
TAG=v0.3.1
RID=linux-x64      # linux-arm64, win-x64, win-arm64, osx-x64, osx-arm64
gh release download "$TAG" -R dantte-lp/arista-mcp \
  -p "arista-mcp-${TAG}-${RID}.tar.gz"
tar -xzf "arista-mcp-${TAG}-${RID}.tar.gz"
cd "arista-mcp-${TAG}-${RID}"
sudo bash scripts/install.sh
```

The script is idempotent and performs eight phases:

1. Stages the binary at `/opt/apps/arista-mcp-${TAG}-linux-x64/`,
   symlinks `/opt/apps/arista-mcp-current` and `/usr/local/bin/arista-mcp`.
2. Fetches the ONNX models into
   `${MODELS_DIR:-/var/lib/arista-mcp/models}` (skipped when
   `embedder/model.onnx` already exists).
3. Calls `arista-mcp bootstrap --release "$TAG"` — provisions a Podman
   Postgres container, downloads the corpus dump from the release
   attachment, runs `pg_restore --clean --if-exists -j 4`. Skippable
   via `SKIP_BOOTSTRAP=1` when the target DB is already populated.
4. Registers `mcpServers.arista-mcp` in `~/.claude.json` via `jq`.
5. Symlinks the install dir into
   `~/.claude/plugins/marketplaces/arista-mcp/` so
   `/plugin install arista-mcp` inside Claude Code activates the
   slash commands + skill.
6. `codex mcp add arista-mcp -- …` registers the MCP server for Codex.
7. Symlinks `plugin/skills/*` into `~/.codex/skills/`.
8. Prints a summary + smoke-check hints.

Env-var overrides: `TAG=vX.Y.Z`, `MODELS_DIR=…`, `CONN_STRING=…`,
`SKIP_BOOTSTRAP=1`.

## Manual bootstrap — the `arista-mcp bootstrap` verb

```bash
gh release download v0.3.1 -R dantte-lp/arista-mcp \
  -p 'arista-mcp-v0.3.1-linux-x64.tar.gz'
tar -xzf arista-mcp-v0.3.1-linux-x64.tar.gz
cd arista-mcp-v0.3.1-linux-x64
pwsh scripts/fetch-models.ps1                      # ONNX assets ~530 MB
./arista-mcp bootstrap --release v0.3.1 --quadlet  # Linux: --quadlet adds
                                                   # systemd auto-restart units
./arista-mcp serve --transport stdio
```

`bootstrap` flags (see `src/AristaMcp.Cli/Commands/BootstrapCommand.cs`):

| Flag | Default | Notes |
|---|---|---|
| `--release <tag>` | none — restore skipped | Downloads `arista-corpus-<tag>.dump` from the release attachment. |
| `--quadlet` | off | Linux only — copies `deploy/quadlet/*.container|.network|.volume` under `~/.config/containers/systemd/` and `systemctl --user enable --now` the PG unit. |
| `--pg-image <ref>` | `docker.io/tensorchord/vchord-suite:pg18-latest` | Override the upstream Postgres image. |
| `--container-name <name>` | `arista-mcp-postgres` | Podman/Docker container name. |
| `--host-port <port>` | `5434` | Port to publish PG 5432 on. |
| `--skip-pg` | off | Assume PG is already running and reachable via `ARISTA_MCP__ConnectionString`. |
| `--skip-restore` | off | Provision PG but leave the DB empty (you'll `ingest` yourself). |

`bootstrap` is idempotent — re-running it against an existing container
starts the container if stopped, then either restores over the top with
`pg_restore --clean --if-exists` (if `--release` given) or exits after
verifying the container is healthy. Under `/dev/shm` pressure the
serial HNSW rebuild fallback fires automatically and its exit code is
checked (v0.3.1+).

## Source build — the manual four-step path

Requirements when building from source:

- **.NET SDK 10.0.301+** (pinned in `global.json`).
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

### 1 — PostgreSQL with vector + BM25 extensions

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

### 2 — Fetch ONNX models

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

### 3 — Schema

```bash
dotnet ef database update \
  --project src/AristaMcp.Data \
  --startup-project src/AristaMcp.Data
```

This provisions `documents`, `chunks` (with `halfvec(768)` embedding +
`bm25v bm25vector` + the BM25 trigger), `ingest_runs`, the HNSW
`halfvec_cosine_ops` index and the `idx_chunks_bm25` index.

### 4 — Ingest

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

### 5 — Run the MCP server

#### Stdio (Claude Desktop / Claude Code)

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

#### HTTP (curl / raw clients)

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

### 6 — Smoke-test retrieval

```bash
dotnet run --project src/AristaMcp.Cli -- bench \
  --queries tests/fixtures/bench-queries-v2.json \
  --limit 10 \
  --history tests/fixtures/bench-history.jsonl \
  --label my-first-bench
```

Expect top-1 around **≈93.9 %** on the v2 set against the fine-tuned
INT8 bge-reranker (v0.3.1 shipped baseline). Full methodology in
[benchmarking.md](benchmarking.md).

## Configuration reference

Every `AristaMcpSettings` property can be overridden by environment
variable (prefix `ARISTA_MCP__`, use `__` for nesting) or by an
`arista-mcp.json` file in the working directory. Common overrides:

| Env var                                     | Default                                                                                |
|---------------------------------------------|----------------------------------------------------------------------------------------|
| `ARISTA_MCP__ConnectionString`              | `Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista`              |
| `ARISTA_MCP__ModelsDir`                     | `models`                                                                                |
| `ARISTA_MCP__RerankerDir`                   | unset — falls back to `${ModelsDir}/reranker`. Point at `reranker-finetuned-int8` for the production baseline. |
| `ARISTA_MCP__EmbeddingVariant`              | `fp32` (swap to `fp16` for ~1.5–2× CPU speedup at ≤1 pp nDCG cost)                      |
| `ARISTA_MCP__Gpu`                           | `false`                                                                                 |
| `ARISTA_MCP__HttpPort`                      | `8080`                                                                                  |
| `ARISTA_MCP__HttpBind`                      | `127.0.0.1` (set to `0.0.0.0` on a hardened host to expose HTTP externally)             |
| `ARISTA_MCP__MultiQuery__Enabled`           | `false` — rule-based query expansion; regressed on the v2 bench, kept behind a flag     |
| `ARISTA_MCP__ListwiseRerank__Enabled`       | `false` — listwise LLM top-5 re-rank via HyDE endpoint; regressed on v2, kept behind flag |
| `ARISTA_MCP__Hyde__Enabled`                 | `false` — set `true` to switch on HyDE via the llm sidecar                              |
| `ARISTA_MCP__Hyde__Endpoint`                | `http://127.0.0.1:8090/v1/chat/completions`                                             |
| `ARISTA_MCP__Otel__Endpoint`                | unset — set to `http://localhost:4317` to ship OTLP spans to Jaeger                     |

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
