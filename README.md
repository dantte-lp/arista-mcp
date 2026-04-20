# arista-mcp

**Hybrid retrieval MCP server for Arista documentation.** .NET 10 · ModelContextProtocol 1.2 · PostgreSQL 18 with pgvector + vchord_bm25 · ONNX Runtime.

Consumes the catalog produced by [`arista-docs`](../arista-docs) (MD + per-doc JSON) and serves it to Claude or any MCP client via five tools (`search_docs`, `lookup_section`, `list_documents`, `get_document`, `get_status`). Runs over stdio (for Claude Desktop / Claude Code) or Streamable HTTP.

Retrieval is dense (snowflake-arctic-embed-m-v1.5, halfvec(768), HNSW `halfvec_cosine_ops`) × sparse (vchord_bm25 with a custom-model tokenizer) fused via Reciprocal Rank Fusion (k=60) and reranked with a BERT cross-encoder (ms-marco-MiniLM-L6-v2). Queries get Arista acronym annotations (EVPN → "Ethernet VPN", MLAG → "Multi-chassis Link Aggregation", …) before embedding.

---

## Requirements

- **.NET SDK 10.0.201+** (pinned via `global.json`)
- **Podman** with `podman compose` (or Docker Compose v2) on WSL2 or native Linux
- **PowerShell 7+** (for `scripts/fetch-models.ps1`; bash port is straightforward)
- ~**5 GB disk** — models (~530 MB), PostgreSQL data (~1–3 GB for full catalog), build artifacts
- Optional: CUDA 12+ if you want `--gpu` embedding acceleration

## Quickstart

    # 1. Bring up PostgreSQL 18 with pgvector + vchord_bm25 + pg_tokenizer
    podman compose -f docker/compose.yaml up -d postgres

    # 2. Download the ONNX models (~530 MB)
    pwsh scripts/fetch-models.ps1

    # 3. Provision the schema (documents, chunks, ingest_runs, bm25v trigger, HNSW/BM25 indexes)
    dotnet ef database update --project src/AristaMcp.Data --startup-project src/AristaMcp.Data

    # 4. Ingest the arista-docs catalog (or a single category while you iterate)
    dotnet run --project src/AristaMcp.Cli -- ingest
    dotnet run --project src/AristaMcp.Cli -- ingest --category avd     # small test slice

    # 5. Serve over stdio (Claude Desktop / Claude Code)
    dotnet run --project src/AristaMcp.Cli -- serve --transport stdio

    # …or over HTTP for local experiments
    dotnet run --project src/AristaMcp.Cli -- serve --transport http --port 8080

## Windows / Podman note

Default Podman on WSL2 binds published ports at `127.0.0.1` inside the WSL VM — Windows localhost can't reach them. Either:

- **One-time fix** (preferred): `podman machine stop && podman machine set --user-mode-networking && podman machine start`
- **Or use the WSL IP**: find it with `wsl -d podman-machine-default -- ip -4 addr show eth0` and set `ARISTA_MCP__ConnectionString="Host=<wsl-ip>;Port=5434;…"` (or `ARISTA_MCP_TEST_CS` for tests).

`docker/compose.yaml` already binds `0.0.0.0:5434` inside the VM so you reach it via the `vEthernet (WSL)` adapter.

## Connecting Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "arista": {
      "command": "dotnet",
      "args": [
        "run", "--project", "C:\\SHARE\\arista-mcp\\src\\AristaMcp.Cli", "--",
        "serve", "--transport", "stdio"
      ]
    }
  }
}
```

For HTTP mode the client connects to `http://127.0.0.1:8080/` (MCP Streamable HTTP, stateless).

## Configuration

Every `AristaMcpSettings` property can be overridden by environment variable (prefix `ARISTA_MCP__`) or by an `arista-mcp.json` file in the working directory. Common overrides:

| Env | Default |
|-----|---------|
| `ARISTA_MCP__ConnectionString` | `Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista` |
| `ARISTA_MCP__ModelsDir` | `models` |
| `ARISTA_MCP__Gpu` | `false` |
| `ARISTA_MCP__HttpPort` | `8080` |
| `ARISTA_MCP__IngestBatchSize` | `32` |

## MCP tools

| Tool | Purpose |
|------|---------|
| `search_docs` | Hybrid search; returns ranked chunks + optional diagnostics |
| `lookup_section` | Full text of a named section across its chunks |
| `list_documents` | List docs filtered by category / product |
| `get_document` | Full metadata + chunk count for one doc |
| `get_status` | Counts + last ingest run |

## Architecture

```
  AristaMcp.Cli ──► AristaMcp.Server ──► AristaMcp.Core
                  ├─► AristaMcp.Embedding ─► Core
                  └─► AristaMcp.Data      ─► Core
```

- **Core** — domain records, `IChunker`, `SectionAwareChunker`, `QueryExpander`, `IReranker`, `NoopReranker`, `CatalogReader`, `DocumentLoader`.
- **Embedding** — `OnnxEmbedder` (snowflake-arctic-embed-m-v1.5), `OnnxReranker` (ms-marco-MiniLM-L6-v2), `BertWordPieceTokenizer`.
- **Data** — EF Core 9 DbContext, entities, migrations, repositories, Npgsql `DataSourceFactory`.
- **Server** — shared DI (`ServerHosting`), stdio (`StdioHost`) and HTTP (`HttpHost`) entrypoints, `HybridRetriever`, five MCP tool classes.
- **Cli** — `System.CommandLine 2.0.6` root with `ingest`, `serve`, `bench` verbs.

## Development

    dotnet build                  # clean; enforces Meziantou/Roslynator/SonarAnalyzer/Banned-API
    dotnet test                   # unit + integration (requires the podman postgres running)
    dotnet run --project src/AristaMcp.Cli -- bench --limit 10   # retrieval smoke over catalog

`CLAUDE.md` has the architecture notes + gotchas (EF Core version pin, WSL port caveat, analyzer suppressions, Sprint-level additions). Sprint plans live under `docs/superpowers/plans/`.

## License

TBD.
