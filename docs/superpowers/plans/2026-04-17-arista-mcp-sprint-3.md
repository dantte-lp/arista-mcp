# arista-mcp Sprint 3 Implementation Plan — Retrieval + MCP Server

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans.

**Goal:** Ship the hybrid retriever and MCP tool surface over both stdio and Streamable HTTP transports, so a Claude client can search Arista documentation through `arista-mcp serve`.

**Architecture:** `Core` gains `QueryExpander` + `HybridRetriever` (depends on `IEmbedder` + `IReranker` interfaces in Core). `Embedding` adds `OnnxReranker`. `Server` hosts the MCP tools and both transports. `Cli` adds the `serve` verb.

**Tech Stack:** ModelContextProtocol 1.2.0 (C# SDK) · ModelContextProtocol.AspNetCore 1.2.0 (Streamable HTTP) · bge-reranker-base (ONNX cross-encoder) · vchord_bm25 `<&>` operator · Pgvector `CosineDistance`.

**Reference spec:** `docs/superpowers/specs/2026-04-16-arista-mcp-design.md`.

---

## Sprint 3 Overview

| # | Task | Outcome |
|---|------|---------|
| 3.1 | QueryExpander | Arista synonym expansion (EVPN/VXLAN/MLAG/BGP/OSPF/LACP/sFlow/SR/MSS/AVD/EOS/CVP/DMF) |
| 3.2 | Reranker | `IReranker` + `OnnxReranker` (bge-reranker-base) + `MockReranker` for tests |
| 3.3 | HybridRetriever | dense + sparse SQL parallel → RRF(k=60) → rerank → quality safeguards |
| 3.4 | MCP tools | `search_docs`, `lookup_section`, `list_documents`, `get_document`, `get_status` |
| 3.5 | Server hosts | stdio Host + ASP.NET Core `MapMcp()` over Streamable HTTP |
| 3.6 | CLI `serve` | `arista-mcp serve --transport stdio\|http --port 8080` |
| 3.7 | Sprint gate | Full test run, end-to-end search assertion, `sprint-3-review` tag |

**Definition of Done:**
- [ ] `dotnet build` clean on all 10 projects
- [ ] `dotnet test` all green (Sprint 1 + 2 + 3)
- [ ] `HybridRetrieverIntegrationTest` — loads a fixture corpus, `SearchAsync("EVPN overlay")` returns the MLAG+EVPN chunk as top-1
- [ ] `SearchToolInProcessTest` — exercises `search_docs` via the MCP in-memory transport
- [ ] `arista-mcp serve --transport stdio` boots without errors (smoke)
- [ ] `arista-mcp serve --transport http --port 0` boots, `GET /mcp` (or the SDK's route) responds, teardown clean

**Reranker download:** bge-reranker-base ONNX + tokenizer live under `models/reranker/`. Logic tests use `MockReranker`; real reranker tests skip via `SkippableFact` when absent.

---

## Task 3.1: QueryExpander

**Files:**
- Create: `src/AristaMcp.Core/Retrieval/QueryExpansion.cs`
- Create: `src/AristaMcp.Core/Retrieval/QueryExpander.cs`
- Create: `tests/AristaMcp.Core.Tests/Retrieval/QueryExpanderTests.cs`

Produces a `QueryExpansion` `{ Original, Expanded }` — `Expanded` is the original query with discovered acronyms annotated in parentheses on first use (e.g. "EVPN" → "EVPN (Ethernet VPN)"). Keeps the original for logs.

Tests:
- `"EVPN overlay"` → `"EVPN (Ethernet VPN) overlay"` (case-insensitive, preserves original casing)
- `"mlag configuration"` → `"mlag (Multi-chassis Link Aggregation) configuration"`
- `"cake recipe"` → unchanged
- Multi-term query expands each known term once on first occurrence

## Task 3.2: Reranker

**Files:**
- Create: `src/AristaMcp.Core/Retrieval/IReranker.cs`
- Create: `src/AristaMcp.Core/Retrieval/RerankCandidate.cs` + `RerankResult.cs`
- Create: `src/AristaMcp.Embedding/OnnxReranker.cs`
- Create: `tests/AristaMcp.Embedding.Tests/OnnxRerankerTests.cs` (SkippableFact)
- Create: `tests/AristaMcp.Data.Tests/Retrieval/NoopRerankerTests.cs` — exercise the passthrough path

Reranker takes `{query, candidates[]}` → scored candidates sorted desc. bge-reranker-base is a cross-encoder: takes `[query, doc]` tokenized as one sequence via `BertTokenizer`'s `EncodeToIds(text, textPair, ...)` (or concatenation with [SEP]); ONNX outputs `logits [B, 1]` — single raw score per pair, higher = better.

`MockReranker : IReranker` (test helper) returns deterministic scores based on string-length similarity so retrieval tests are fully isolated.

`NoopReranker : IReranker` (production fallback when model absent) passes candidates through unchanged — used when `models/reranker/` is missing.

## Task 3.3: HybridRetriever

**Files:**
- Create: `src/AristaMcp.Core/Retrieval/RetrievalOptions.cs`
- Create: `src/AristaMcp.Core/Retrieval/IHybridRetriever.cs`
- Create: `src/AristaMcp.Core/Retrieval/HybridRetriever.cs`
- Create: `tests/AristaMcp.Data.Tests/Retrieval/HybridRetrieverIntegrationTest.cs`

Flow:

1. Expand the query via `QueryExpander`.
2. Embed (with query prefix).
3. In parallel:
   - **dense SQL:** `ORDER BY embedding <=> $1::halfvec LIMIT @k` (pgvector cosine)
   - **sparse SQL:** `ORDER BY bm25v <&> to_bm25query('idx_chunks_bm25', tokenize($1, 'chunks_tokenizer')::bm25vector) LIMIT @k`
4. Fuse via RRF with k=60: `score = Σ 1 / (k + rank_i)`.
5. Rerank top-N via `IReranker`.
6. Quality safeguards:
   - Filter by min score if configured
   - Drop near-duplicate (same `(document_id, section_title, first 200 chars)` key)
7. Return `SearchResponse` with `SearchDiagnostics` populated.

The retriever takes repositories (`IChunkRepository` already exists) + `NpgsqlDataSource` for raw ADO queries (EF doesn't express `<=>` or `<&>` cleanly).

Test seeds a small corpus through the ingest pipeline, then asserts retrieval ordering.

## Task 3.4: MCP tools

**Files:**
- Create: `src/AristaMcp.Server/Tools/SearchDocsTool.cs`
- Create: `src/AristaMcp.Server/Tools/LookupSectionTool.cs`
- Create: `src/AristaMcp.Server/Tools/ListDocumentsTool.cs`
- Create: `src/AristaMcp.Server/Tools/GetDocumentTool.cs`
- Create: `src/AristaMcp.Server/Tools/GetStatusTool.cs`
- Create: `tests/AristaMcp.Server.Tests/SearchDocsToolInProcessTest.cs`

Each tool is an `[McpServerToolType]` class with instance ctor injection. Methods marked `[McpServerTool, Description("…")]` with `[Description]` on each parameter.

`search_docs`:
- Params: `query` (string), `top_k` (int, default 10, max 50), `category` (optional), `with_diagnostics` (bool)
- Calls `IHybridRetriever.SearchAsync(…)`, returns JSON with `results[]` + optional `diagnostics`

`lookup_section`:
- Params: `document_id`, `section_title`
- Returns the concatenated `raw_content` of all chunks in that (document, section)

`list_documents`:
- Params: `category?`, `product?`, `limit?`
- Returns documents matching the filter

`get_document`:
- Params: `document_id`
- Returns the single document metadata + chunk count

`get_status`:
- Returns counts + last ingest run info + extension versions

Tests use `McpClient.CreateAsync(new InMemoryTransport())` paired with an in-process `IMcpServer`.

## Task 3.5: Server hosts (stdio + HTTP)

**Files:**
- Modify: `src/AristaMcp.Server/Program.cs` (Web-SDK shell, `app.MapMcp()`)
- Create: `src/AristaMcp.Server/ServerHosting.cs` (shared DI registration)
- Create: `src/AristaMcp.Server/StdioHost.cs` (console Host wiring for stdio)
- Create: `tests/AristaMcp.Server.Tests/HttpTransportBootTest.cs` (starts `WebApplication` on ephemeral port, issues one request, shuts down)

Shared DI registers: `NpgsqlDataSource` + `AristaDbContext` factory + `IEmbedder` (OnnxEmbedder) + `IReranker` (OnnxReranker or NoopReranker) + repositories + `QueryExpander` + `HybridRetriever` + tool classes.

For stdio: `Host.CreateApplicationBuilder` + `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`. Logging configured to write to stderr (stdout is the MCP transport).

For HTTP: `WebApplication.CreateBuilder` + `AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithToolsFromAssembly()` + `app.MapMcp()`.

## Task 3.6: CLI `serve` verb

**Files:**
- Create: `src/AristaMcp.Cli/Commands/ServeCommand.cs`
- Modify: `src/AristaMcp.Cli/Program.cs` — register the new command

`arista-mcp serve --transport stdio|http --port 8080`. For stdio mode the CLI hands off to the Server's stdio entrypoint; for HTTP mode it launches the Server's ASP.NET Core host.

## Task 3.7: Sprint 3 gate + tag

1. Full `dotnet build` + `dotnet test`.
2. Run `arista-mcp serve --transport stdio` briefly and confirm stderr shows tool registration.
3. Run `arista-mcp serve --transport http --port 8090` briefly and confirm the MCP endpoint responds.
4. Update `CLAUDE.md` Sprint 3 section.
5. `git tag sprint-3-review`.

## Gate checklist

- [ ] `dotnet build` clean
- [ ] `dotnet test` — Sprint 1 + 2 + 3 all green
- [ ] `HybridRetrieverIntegrationTest` passes (real corpus via ingest fixture, ordering asserted)
- [ ] `SearchDocsToolInProcessTest` passes (in-memory MCP client)
- [ ] `HttpTransportBootTest` passes (ephemeral ASP.NET Core)
- [ ] CLI `serve --help` renders; both transports boot
- [ ] `sprint-3-review` tag exists
