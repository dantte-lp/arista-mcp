# arista-mcp — integration guide for MCP clients

How to wire arista-mcp into Claude Desktop, Claude Code, and any raw MCP
client. Covers tool schemas, typical query patterns, and the failure modes
that bite most often on a first install.

See also: [README.md](../README.md) for install + quickstart,
[`docs/otel.md`](otel.md) for tracing, and
[`CLAUDE.md`](../CLAUDE.md) for architecture gotchas.

---

## 1. Transports

arista-mcp ships two transports from the same code path:

| Transport | Client | Command |
|-----------|--------|---------|
| `stdio`   | Claude Desktop, Claude Code, any MCP client that spawns a subprocess | `arista-mcp serve --transport stdio` |
| `http`    | HTTP-native clients, local browsers, `curl`, Inspector | `arista-mcp serve --transport http --bind 0.0.0.0 --port 8080` |

The HTTP host listens on `127.0.0.1` by default. Pass `--bind 0.0.0.0`
(or set `ARISTA_MCP__HttpBind`) to expose it on all interfaces — do this
behind a reverse proxy that adds TLS + auth. A liveness endpoint is
mounted at `GET /v1/healthz` and returns `{"status":"ok"}` when the
embedder, reranker, and Postgres pool are all reachable.

Both use the same DI surface (`ServerHosting.AddAristaMcpServices`), so
tools, embedder, reranker, and retriever behave identically. HTTP is
stateless (`MapMcp(..., Stateless = true)`) — no session affinity needed.

## 2. Claude Desktop

Edit `claude_desktop_config.json`:

- **macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows**: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "arista": {
      "command": "dotnet",
      "args": [
        "run", "--project", "C:\\SHARE\\arista-mcp\\src\\AristaMcp.Cli",
        "--no-build", "--",
        "serve", "--transport", "stdio"
      ],
      "env": {
        "ARISTA_MCP__ConnectionString": "Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista"
      }
    }
  }
}
```

Notes:

- `--no-build` avoids a rebuild on every server spawn (Claude Desktop
  restarts the subprocess aggressively). Run `dotnet build -c Release` once
  and keep the binary warm.
- For a faster cold-start, build a single-file publish and point
  `command` straight at the exe: `dotnet publish src/AristaMcp.Cli -c Release -r win-x64 --self-contained true`.
- Environment overrides live in `env` — useful for pointing at a
  production DB without editing config files.

After save, fully quit and relaunch Claude Desktop. The arista tool set
appears under the 🔧 menu.

## 3. Claude Code

Claude Code writes MCP server registrations into `~/.claude.json`
(per-user) under the `mcpServers` key — the same shape as Desktop:

```bash
claude mcp add arista-mcp \
  -- /usr/local/bin/arista-mcp serve --transport stdio
```

`scripts/install.sh` performs the equivalent `jq` insertion into
`~/.claude.json` automatically. If you edit the file directly, restart
Claude Code so the new server is picked up.

## 4. Raw HTTP (curl / Inspector)

Start the HTTP host:

```bash
dotnet run --project src/AristaMcp.Cli -- serve --transport http --port 8080
```

List tools:

```bash
curl -sN -H 'Accept: application/json, text/event-stream' \
  -H 'Content-Type: application/json' \
  -X POST http://127.0.0.1:8080/ \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Call a tool:

```bash
curl -sN -H 'Accept: application/json, text/event-stream' \
  -H 'Content-Type: application/json' \
  -X POST http://127.0.0.1:8080/ \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search_docs","arguments":{"query":"EVPN type-5 IP prefix","limit":5}}}'
```

Streamable HTTP returns `text/event-stream` — the `-N` flag disables
curl's buffering so you see chunks as they arrive.

## 5. Tool reference

### `search_docs`

Hybrid dense + sparse + rerank retrieval over the catalog.

```jsonc
{
  "name": "search_docs",
  "arguments": {
    "query": "MLAG peer-link setup",
    "topK": 5,             // default 10, clamp 1-50
    "category": "manual",  // optional; one of manual, reference, toi
    "product": "eos",      // optional; exact match on ChunkResult.Product
    "withDiagnostics": false // optional; adds per-stage timings when true
  }
}
```

Typical response (snake_case fields; matches `SearchDocsTool.cs`):

```jsonc
{
  "results": [
    {
      "chunk_id": 12345,
      "document_id": "a1b2c3d4e5f6",
      "document_title": "Arista EOS User Manual",
      "document_slug": "EOS-User-Manual",
      "category": "manual",
      "product": "eos",
      "version": "4.36.0F",
      "section_title": "MLAG Configuration",
      "page_start": 342,
      "page_end": 347,
      "score": 9.81,
      "content": "MLAG (Multi-chassis Link Aggregation) pairs two leaves …"
    }
  ]
}
```

`score` is the reranker's logit (higher = better). Per-stage similarity
components are surfaced only when `withDiagnostics=true`.

### `lookup_section`

Fetches every chunk whose `section_title` matches, across the document.
Useful once `search_docs` has pointed at a specific section and you want
the full body.

```jsonc
{
  "name": "lookup_section",
  "arguments": {
    "doc_id": "a1b2c3d4e5f6",
    "section_title": "MLAG Configuration"
  }
}
```

### `list_documents`

Pagination-friendly listing.

```jsonc
{
  "name": "list_documents",
  "arguments": {
    "category": "toi",     // optional
    "product": "eos",      // optional
    "limit": 50,
    "offset": 0
  }
}
```

### `get_document`

Full metadata for one doc, including chunk count and pdf_sha256.

```jsonc
{ "name": "get_document", "arguments": { "doc_id": "a1b2c3d4e5f6" } }
```

### `get_status`

Server + DB health: last ingest run, chunk counts, bm25v coverage.

```jsonc
{ "name": "get_status" }
```

## 6. Query patterns that work well

The hybrid stack (dense + BM25 + rerank + Arista acronym expansion) has
different strengths per query shape. Some patterns that reliably return
the right doc in the top-3:

### Acronym-heavy

- `"MLAG peer-link active-active"` — MLAG expands to "Multi-chassis Link
  Aggregation" before embedding. Acronym-heavy queries benefit most from
  the `QueryExpander` hop.
- `"BGP EVPN type-5 IP prefix routes"` — stacked acronyms, all expanded.

### Version-scoped

- `"EOS 4.36.0F VXLAN changes"` — `product="eos"` passed as an explicit
  filter narrows to the EOS corpus; the version string is a strong BM25
  hit on release-notes titles.

### Product-disambiguating

- `"CloudVision change control workflow"` — add `product="cvp"` to
  eliminate EOS chapters that mention change-control in a different
  sense.

### Section-specific

- First call: `search_docs` with a broad query to get the doc hit.
- Second call: `lookup_section` with the exact `section_title` from the
  first response — returns the whole section verbatim, no rerank noise.

### Anti-patterns

- Single-word queries (`"BGP"`) are too broad; return many false
  positives. Prefer at least 3 content words.
- Pure command-line snippets (`"show interface counters"`) work better as
  BM25 matches; `search_docs` handles them but the rerank can prefer
  prose. If your use case is command reference, consider adding a
  `EOS-4.36.0F-CommandApiGuide` slug filter via `lookup_section`.
- Questions phrased as natural language (`"how do I configure MLAG?"`)
  get embedded differently than the doc bodies. The QueryExpander helps,
  but you'll get better results with keyword-style phrasing ("MLAG
  configuration peer-link active-active").

## 7. Observability

Set `ARISTA_MCP__Otel__Endpoint=http://localhost:4317` and all five
tools emit traces via the `AristaMcp` ActivitySource. See
[`docs/otel.md`](otel.md) for the Jaeger compose + span schema. Useful
for:

- Debugging slow queries (per-stage timing: embed, dense, sparse, rerank)
- Seeing which queries hit the embedding cache (`arista.cache.hit=true`)
- Tracking adaptive-rerank decisions (`arista.rerank.adaptive=true`)

## 8. Troubleshooting

### "tool call failed: connection refused"

PostgreSQL isn't reachable from wherever `arista-mcp serve` is running.
Check:

```bash
# From the same machine running serve:
podman exec arista-mcp-postgres pg_isready -U arista
# From Windows host (WSL2 case):
podman machine set --user-mode-networking   # one-time
# or set ARISTA_MCP__ConnectionString to the WSL IP (see README)
```

### "embedder model missing at models/embedder"

`scripts/fetch-models.ps1` didn't run, or `ARISTA_MCP__ModelsDir` points
somewhere else. Re-run the script; it downloads into `models/embedder/`
and `models/reranker/`.

### "results are empty"

First, confirm the catalog actually ingested. There is no
`arista-mcp get-status` CLI verb — status is only available via the
`get_status` MCP tool. From a running MCP client:

```
/mcp arista-mcp get_status
```

Or straight against Postgres:

```bash
podman exec arista-mcp-postgres psql -U arista -d arista -c "SELECT COUNT(*) FROM chunks;"
```

If chunks = 0, run `arista-mcp ingest --catalog ../arista-docs/data/catalog.json --force`.

If chunks > 0 but search still empty, the most common cause is that the
catalog contains FakeConverter placeholders — the defensive filter in
`IngestService` skips them silently. Check upstream:

```bash
cd ../arista-docs
uv run arista-docs status --stale-mds
# if flagged docs > 0:
uv run arista-docs purge-fakes
uv run arista-docs sync   # real Marker reconversion (GPU)
```

### "spans not appearing in Jaeger"

OpenTelemetry registration only fires when `ARISTA_MCP__Otel__Endpoint`
or `OTEL_EXPORTER_OTLP_ENDPOINT` is set. In Claude Desktop's config, add
both in the `env` block:

```json
"env": {
  "ARISTA_MCP__ConnectionString": "...",
  "ARISTA_MCP__Otel__Endpoint": "http://localhost:4317"
}
```

For CLI commands (bench, ingest, curate-triples), the imperative
`OtelConfig.BuildTracerProviderIfEnabled()` handles flushing on exit —
if you see partial traces, confirm the command exited cleanly (not
killed mid-run).

### "status=partial on ingest"

Some doc errored out. Inspect:

```bash
podman exec arista-mcp-postgres psql -U arista -d arista -c \
  "SELECT error_msg FROM ingest_runs WHERE status='partial' ORDER BY started_at DESC LIMIT 1;"
```

Most common: MD or JSON missing on disk under `catalog.base_dir`. Re-run
`arista-docs sync` to refresh the converted artefacts.
