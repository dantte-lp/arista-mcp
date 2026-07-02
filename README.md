# arista-mcp

Hybrid retrieval MCP server for Arista documentation.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-18-4169E1?logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![pgvector](https://img.shields.io/badge/pgvector-0.8.2-336791)](https://github.com/pgvector/pgvector)
[![ONNX Runtime](https://img.shields.io/badge/ONNX_Runtime-1.24-005CED)](https://onnxruntime.ai/)
[![MCP](https://img.shields.io/badge/MCP-1.2-blueviolet)](https://modelcontextprotocol.io/)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

**[English docs](docs/en/) · [Документация на русском](docs/ru/) · [Changelog](CHANGELOG.md)**

---

`arista-mcp` consumes the catalog produced by [`arista-docs`](https://github.com/dantte-lp/arista-docs)
(Markdown + per-document JSON) and serves it to Claude or any MCP client via
five tools — `search_docs`, `lookup_section`, `list_documents`, `get_document`,
`get_status`. Runs over stdio (Claude Desktop / Claude Code) or Streamable HTTP.

Retrieval is a three-stage pipeline: **dense** (`snowflake-arctic-embed-m-v1.5`
through ONNX Runtime, `halfvec(768)`, HNSW `halfvec_cosine_ops`) and **sparse**
(`vchord_bm25` with a custom-model tokenizer) run in parallel, fuse via
**Reciprocal Rank Fusion** at k = 60, then the top-N are **reranked** by a
BERT cross-encoder (`ms-marco-MiniLM-L6-v2`). Queries get Arista acronym
annotations before embedding.

## Architecture at a glance

```mermaid
flowchart LR
  subgraph clients["MCP clients"]
    claude[Claude Desktop / Code]
    http[HTTP caller]
  end

  subgraph server["arista-mcp"]
    stdio["StdioHost<br/><i>stdin/stdout JSON-RPC</i>"]
    httpHost["HttpHost<br/><i>Streamable HTTP /mcp</i>"]
    tools["5 MCP tools"]
    retriever["HybridRetriever"]
    embedder["OnnxEmbedder<br/><i>arctic-embed-m</i>"]
    reranker["OnnxReranker<br/><i>MiniLM-L6</i>"]
  end

  subgraph data["PostgreSQL 18"]
    pgv["pgvector HNSW<br/><i>halfvec(768)</i>"]
    bm25["vchord_bm25<br/><i>tokenized column</i>"]
  end

  claude -- stdio --> stdio
  http --> httpHost
  stdio --> tools
  httpHost --> tools
  tools --> retriever
  retriever --> embedder
  retriever --> pgv
  retriever --> bm25
  retriever --> reranker
```

See **[docs/en/architecture.md](docs/en/architecture.md)** for layering rules
and detailed component / sequence diagrams.

## Retrieval pipeline

```mermaid
flowchart LR
  Q[query] --> QE[QueryExpander<br/><i>Arista acronyms</i>]
  QE --> HyDE["HyDE<br/><i>opt-in, Enabled=false</i>"]
  HyDE -- dense only --> EMB[OnnxEmbedder]
  QE -- raw --> BM25sql[(vchord_bm25<br/>SQL)]
  EMB --> DNSsql[(pgvector HNSW<br/>SQL)]
  DNSsql --> RRF[Reciprocal Rank<br/>Fusion k=60]
  BM25sql --> RRF
  RRF -- top-N --> RR[OnnxReranker]
  RR --> Results[ranked chunks]
```

Deep dive in **[docs/en/retrieval.md](docs/en/retrieval.md)**.

## Quick start — pre-built binary + corpus dump

Linux / macOS / Windows. No `dotnet` SDK, no `git clone`, no re-ingest.
Each release ships a self-contained single-file binary for all 6 RIDs
plus a `pg_restore`-able corpus dump.

### One-command install (Linux)

```bash
TAG=v0.3.1
RID=linux-x64
gh release download "$TAG" -R dantte-lp/arista-mcp \
  -p "arista-mcp-${TAG}-${RID}.tar.gz"
tar -xzf "arista-mcp-${TAG}-${RID}.tar.gz"
cd "arista-mcp-${TAG}-${RID}"
sudo bash scripts/install.sh
```

`scripts/install.sh` stages the binary under `/opt/apps/arista-mcp-${TAG}-linux-x64/`,
symlinks `/usr/local/bin/arista-mcp`, fetches ONNX models into
`/var/lib/arista-mcp/models`, runs `arista-mcp bootstrap --release "$TAG"`
(Postgres container + corpus restore), registers the MCP server in
`~/.claude.json` and via `codex mcp add`, and links the Claude Code
plugin marketplace. Idempotent. Env-var overrides: `TAG=vX.Y.Z`,
`MODELS_DIR=…`, `CONN_STRING=…`, `SKIP_BOOTSTRAP=1`.

### Linux / macOS — manual bootstrap

```bash
# 1. Pick the right RID — linux-x64, linux-arm64, osx-x64, osx-arm64.
RID=linux-x64
TAG=v0.3.1
gh release download "$TAG" -R dantte-lp/arista-mcp \
  -p "arista-mcp-${TAG}-${RID}.tar.gz" -p "arista-mcp-${TAG}-${RID}.tar.gz.sha256"
sha256sum -c "arista-mcp-${TAG}-${RID}.tar.gz.sha256"
tar -xzf "arista-mcp-${TAG}-${RID}.tar.gz"
cd "arista-mcp-${TAG}-${RID}"

# 2. ONNX models (~530 MB embedder + reranker).
pwsh scripts/fetch-models.ps1            # needs PowerShell 7+ on linux/macOS

# 3. One-shot bootstrap: pulls postgres (vchord-suite pg18), restores
#    the corpus dump from the release attachment, runs pg_restore.
#    Idempotent — re-running starts the existing container; restore
#    is skipped when --release is omitted on a follow-up run.
./arista-mcp bootstrap --release "$TAG" --quadlet    # Linux: --quadlet adds systemd auto-restart
./arista-mcp bootstrap --release "$TAG"              # macOS: no Quadlet

# 4. Serve — stdio for Claude Desktop / Claude Code.
./arista-mcp serve --transport stdio
```

### Windows

```powershell
$TAG = 'v0.3.1'
$RID = 'win-x64'                 # or win-arm64
gh release download $TAG -R dantte-lp/arista-mcp `
  -p "arista-mcp-$TAG-$RID.zip" -p "arista-mcp-$TAG-$RID.zip.sha256"
Get-FileHash "arista-mcp-$TAG-$RID.zip" -Algorithm SHA256
Expand-Archive "arista-mcp-$TAG-$RID.zip" .
Set-Location "arista-mcp-$TAG-$RID"

pwsh scripts\fetch-models.ps1

# Bootstrap — assumes Podman Desktop or Docker Desktop is running.
.\arista-mcp.exe bootstrap --release $TAG

# Optional: register as a Windows Service (auto-start on boot).
pwsh deploy\windows\Install-AristaMcpService.ps1 `
  -BinaryPath "$PWD\arista-mcp.exe" `
  -ConnectionString 'Host=127.0.0.1;Port=5434;Database=arista;Username=arista;Password=arista' `
  -ModelsDir "$PWD\models"
```

### Container (any OS)

```bash
podman pull ghcr.io/dantte-lp/arista-mcp:v0.3.1
# Cosign verify (keyless OIDC signature is added by the release pipeline):
cosign verify ghcr.io/dantte-lp/arista-mcp:v0.3.1 \
  --certificate-identity-regexp '.+' \
  --certificate-oidc-issuer-regexp '.+'
```

Use `deploy/quadlet/` for a full Quadlet-managed stack (PG + server)
or run the image directly against an externally-managed Postgres.

### Source build (contributors only)

```bash
podman compose -f docker/compose.yaml up -d postgres
pwsh scripts/fetch-models.ps1
dotnet ef database update --project src/AristaMcp.Data
dotnet run --project src/AristaMcp.Cli -- ingest                    # ~25 min full corpus
dotnet run --project src/AristaMcp.Cli -- serve --transport stdio
```

Walk-through, troubleshooting, client configs →
**[docs/en/getting-started.md](docs/en/getting-started.md)**.

## MCP tools

| Tool              | Purpose                                                          |
|-------------------|------------------------------------------------------------------|
| `search_docs`     | Hybrid search — returns ranked chunks + optional diagnostics     |
| `lookup_section`  | Full text of a named section across its chunks                   |
| `list_documents`  | Filter documents by category / product                           |
| `get_document`    | Full metadata + chunk count for one document                     |
| `get_status`      | Chunk / document counts and last ingest-run summary              |

Full schemas, example payloads, query patterns → **[docs/en/mcp-tools.md](docs/en/mcp-tools.md)**.

## CLI verbs

| Verb                      | Purpose                                                                 |
|---------------------------|-------------------------------------------------------------------------|
| `arista-mcp ingest`       | Chunk + embed + upsert an `arista-docs` catalog into PostgreSQL         |
| `arista-mcp serve`        | Run the MCP server (`--transport stdio\|http`, `--bind`, `--port N`)    |
| `arista-mcp bootstrap`    | One-shot: provision postgres + `pg_restore` the corpus dump from a release attachment (+ optional Quadlet on Linux) |
| `arista-mcp bench`        | Retrieval bench with per-run JSONL history (`--history`, `--label`)     |
| `arista-mcp curate-triples`       | Emit `(query, positive, hard-negatives)` for cross-encoder tuning |
| `arista-mcp validate-bench-queries` | Fairness-filter LLM-generated bench queries via the retriever   |

## Documentation

| Doc                                                      | Audience               |
|----------------------------------------------------------|------------------------|
| [docs/en/architecture.md](docs/en/architecture.md)       | Component / layer map  |
| [docs/en/retrieval.md](docs/en/retrieval.md)             | How hybrid search works end-to-end |
| [docs/en/getting-started.md](docs/en/getting-started.md) | Hands-on setup         |
| [docs/en/mcp-tools.md](docs/en/mcp-tools.md)             | Tool reference         |
| [docs/en/benchmarking.md](docs/en/benchmarking.md)       | Bench v2 methodology   |
| [docs/en/development.md](docs/en/development.md)         | Build, test, conventions |

Russian translation: **[`docs/ru/`](docs/ru/)**.

Historical / operational notes: [`docs/mcp-integration.md`](docs/mcp-integration.md),
[`docs/onnx-provider.md`](docs/onnx-provider.md), [`docs/otel.md`](docs/otel.md).

## Release artefacts

Every tag (`vX.Y.Z`) attaches this matrix to the GitHub release page:

| Artefact | RIDs | Description |
|---|---|---|
| `arista-mcp-vX.Y.Z-<rid>.tar.gz` + `.sha256` | `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` | Self-contained binary + `plugin/` + `scripts/` |
| `arista-mcp-vX.Y.Z-<rid>.zip` + `.sha256` | `win-x64`, `win-arm64` | Same payload, Windows-shape |
| `arista-corpus-vX.Y.Z.dump` + `.sha256` | — | `pg_restore --format=custom` binary dump |
| `ghcr.io/dantte-lp/arista-mcp:vX.Y.Z` | `linux/amd64`, `linux/arm64` | Cosign keyless-OIDC signed multi-arch image |

The corpus dump is version-independent unless the ingest pipeline
changes; `bootstrap` automatically resolves the newest dump for the
requested tag (with a fallback to the prior corpus tag) if the release
did not re-run ingest.

## Current status

- **v0.3.1** shipped — first release from the multi-RID pipeline
  (6 RID single-file binaries + multi-arch container on GHCR +
  cosign keyless + corpus dump carry-forward). Retrieval baseline
  on the 570-query v2 fixture: **top-1 93.86 % / top-10 100 % /
  p50 3.4 s / p95 4.5 s** on CPU with the `bge-reranker-v2-m3` INT8
  reranker (see `plan v0.3-revised` in the docs).
- v0.3.0 shipped the install-readiness infrastructure (`bootstrap`
  verb, `scripts/install.sh`, Claude Code marketplace, Quadlet
  templates). See [CHANGELOG.md](CHANGELOG.md) for the full list.
- See [CHANGELOG.md](CHANGELOG.md) for the full version history.

## License

MIT — see [`LICENSE`](LICENSE).
