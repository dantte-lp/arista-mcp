# arista-mcp Roadmap — v0.2 and Beyond

**State at v0.1.2:** production-ready MCP server; 2426/2426 docs + 9569 chunks
indexed; top-10 hit rate 86.7% on a 30-query bench; CPU-only inference at p95 2.3 s;
EF Core 9 (blocked), FluentAssertions 8 license flag, 4 bench-query curation gaps.

This roadmap captures the next three sprints (7, 8, 9) targeted at `v0.2.0`, plus a
longer backlog.

---

## Sprint 7 — Retrieval quality + GPU (target: **v0.1.3**)

**Goal:** uplift the retrieval baseline from 86.7% top-10 to ≥ 95% with
measurable per-change deltas and cut p95 latency from 2.3 s to under 400 ms.

### 7.1 — Fix the 4 bench-query misses

Concrete issue: `hardware`, `aboot`, `CVA`, `CVW` queries' `expect_any` tokens do
not match any slug in the catalog. Retrieval may well be returning the right doc;
the matcher substring test fails.

- Audit `bench-queries.json` against the catalog: run each query, inspect the
  top-10, manually confirm whether the right doc is there under a different slug.
- Rewrite `expect_any` per query to use either slug fragments that truly exist,
  or product field equality (extend `BenchmarkQuery` with an optional
  `expect_product` field that matches `ChunkResult.Product` directly).
- Re-bench; expect top-10 ≥ 95 %.

### 7.2 — Expand the bench query set to 100

30 queries is a smoke; 100 gives enough statistical resolution to catch 3 pp
regressions. Target split:
- 40 EOS/NOS CLI-style questions ("show interface …", "how do I configure …")
- 20 CVP / CV-CUE / DMF management-plane questions
- 10 hardware / optics / cabling
- 10 MSS / AVD / CloudEOS / VeloCloud
- 20 "feature X" semantic queries (EVPN, VXLAN, sFlow, LANZ, …)

Also add **expected-doc-id** ground truth rows where a single canonical doc is
the right answer (moves toward nDCG@10-style scoring rather than binary hit rate).

### 7.3 — CPU-only latency reduction

**Runtime is CPU-only by design** — GPU is reserved for offline fine-tuning
workflows (Sprint 9+). Latency wins without a GPU:

- **fp16 embedder variant.** `fetch-models.ps1` can swap to
  `onnx/model_fp16.onnx` (~218 MB, half the fp32 size). Expected ~1.5–2× faster
  on modern CPUs with AVX2/AVX-512 while preserving retrieval quality
  (Snowflake's card confirms negligible nDCG@10 delta).
- **Per-session query embedding cache.** Identical repeated queries hit the
  same embedding; memoize the last N (LRU, default 256). Matters for
  conversational flows where Claude issues "EVPN" then "EVPN configuration" —
  both contain the same expanded tokens.
- **Rerank candidate cap + early exit.** Currently `RerankTopN = 30` always.
  Reduce to 20 when the RRF top-5 scores cluster tightly (no value in
  reranking 30 near-duplicates); keep 30 when the fusion is spread.
- **Warm the ONNX session on first request.** First query pays ~200 ms
  graph-init cost currently amortized in each cold serve. Pre-warm at host
  startup with a throwaway embed call.

Target: p95 **2.3 s → ≤ 1.2 s** on CPU; top-10 hit rate unchanged.

### 7.4 — Heading-normalization edge cases

**28 % of chunks don't receive `page_start`** from JSON enrichment. Investigate:
run a diagnostic against every doc that produced unmatched sections, dump the
pairs `(md_cleaned, json_cleaned)` that didn't line up. Expected root causes:
- Unicode variants (em-dash vs en-dash, non-breaking space)
- Leading/trailing punctuation (`:` or `.` at heading tail)
- Level mismatch (MD `##` but JSON `level: 3` due to marker upstream quirks)

Patch `CleanHeading` + matching algorithm; target ≥ 95 % of chunks with
`page_start`.

### 7.5 — Section-aware dedup in search results

Observed: when two chunks from the same section rank in the top-10, users get
redundancy. Add an optional `--dedup-per-section` flag (default off) that keeps
only the highest-scoring chunk per (document_id, section_title) in the output.

### Sprint 7 gate

- [ ] Bench top-10 ≥ 95 % (expanded 100-query set)
- [ ] p95 latency ≤ 1.2 s (CPU, no GPU at runtime)
- [ ] ≥ 95 % of chunks have `page_start`
- [ ] `v0.1.3` tag

---

## Sprint 8 — Operational polish (target: **v0.1.4**)

**Goal:** make arista-mcp consumable without a local .NET 10 SDK; robust
observability; production error handling.

### 8.1 — Docker image for arista-mcp itself

`Dockerfile.app` (already stub-committed in Sprint 1's file list but never
populated). Multi-stage:
- Stage 1: .NET 10 SDK + `dotnet publish -c Release` + trim + AOT where safe
- Stage 2: `mcr.microsoft.com/dotnet/aspnet:10.0` + ONNX Runtime runtime deps
- Mount models/ + arista-docs/ as volumes; env-var config
- Push to `ghcr.io/<owner>/arista-mcp:<tag>`

Updated `docker/compose.yaml` with a second service `mcp` that depends on
`postgres`. Quickstart becomes `podman compose up -d` (two services).

### 8.2 — Structured logging via `LoggerMessage` source generator

Currently zero `ILogger` calls in `src/`. Wire:
- `IngestService` — one log per doc (doc_id, chunks_upserted, elapsed, error?)
- `HybridRetriever` — one log per query (query_cleaned, dense_hits, sparse_hits,
  after_rerank, total_ms)
- `OnnxEmbedder` — one log on model load (path, providers, warmup_ms)

Use `[LoggerMessage]` partial methods for zero-allocation structured logs.
Route to stderr in stdio mode (already done); JSON console in HTTP mode.

### 8.3 — OpenTelemetry (traces + metrics)

Add `OpenTelemetry.Exporter.OpenTelemetryProtocol` + autoinstrumentation for
ASP.NET Core + Npgsql. Metrics:
- `arista_mcp.search.duration_ms` (histogram, labeled by category filter)
- `arista_mcp.search.dense_hits` / `sparse_hits` (histograms)
- `arista_mcp.ingest.chunks_per_second` (gauge)
- `arista_mcp.rerank.score_range` (histogram of top-1 scores)

Disable by default; enable via `ARISTA_MCP__Otel__Endpoint=http://…`. README
section with Jaeger/Tempo/Grafana docker-compose snippet.

### 8.4 — Graceful error classes in tools

MCP tools currently let exceptions propagate. Convert to `CallToolResult
{ IsError = true, Content = … }` with typed error codes (`NOT_FOUND`,
`DB_UNREACHABLE`, `MODEL_MISSING`, `BAD_ARGUMENT`). Add xUnit tests that force
each error path.

### 8.5 — Stale-run detection

`IIngestRunRepository.ListStaleAsync(staleThreshold)` — surfaces ingest runs
with `status='running'` and `started_at < now - threshold`. FakeTimeProvider
test asserts exact boundary (`Advance(1h - 1s)` → not stale; `Advance(2s)` →
stale). New MCP tool `get_ingest_runs` returns last N runs + stale list.

### Sprint 8 gate

- [ ] `ghcr.io/<owner>/arista-mcp:v0.1.4` image pulled and serves cleanly
- [ ] ≥ 1 structured log per query + per ingest-doc
- [ ] OTEL traces visible in a local Jaeger
- [ ] Error path tests pass for all 5 tools
- [ ] `v0.1.4` tag

---

## Sprint 9 — Advanced retrieval (target: **v0.2.0**)

**Goal:** cross-encoder query understanding + multi-hop search + query-time
section expansion. Real `v0.2.0` step change.

### 9.1 — Query classifier (learned expansion)

Replace the hard-coded `QueryExpander.Synonyms` FrozenDictionary with a small
classifier that identifies which Arista product(s) a query touches and annotates
the query accordingly. Options:
- A: keep hand-curated acronym map, add regex-based product extractor
  ("7050X3" → product=eos, platform=7050X3)
- B: train a tiny DistilBERT head on (query, product) pairs from CVP docs
- C: call a small LLM at query time (adds latency)

Path A is cheap + testable, recommended first.

### 9.2 — Section-hierarchy expansion

When a chunk hits, optionally include its parent section's opening paragraph as
additional context in the result. Helps LLM callers ground answers without
follow-up `lookup_section` calls.

New MCP tool param: `search_docs(..., include_section_context: bool = false)`.

### 9.3 — Multi-hop MCP tool: `trace_reference`

When a chunk cross-references another section ("see Chapter 5"), fetch and
return that target. Uses `lookup_section` under the hood. Useful for EOS
configuration guides that heavily cross-reference.

### 9.4 — Hybrid score weighting knobs

Current RRF is fixed at k=60 with equal weighting. Expose in `RetrievalOptions`:
- `DenseWeight: float` (default 1.0)
- `SparseWeight: float` (default 1.0)
- `RrfK: int` (default 60; now tunable per query)

Add a `--sweep` flag to `bench` that grid-searches (dense, sparse, k) and writes
the best combo to history.

### 9.5 — EF Core 10 bump (if upstream shipped)

Check `Pgvector.EntityFrameworkCore` NuGet for a new release. If `0.4.0` /
`10.0.0` is out, bump EF Core 9.x → 10.x, drop the NU1608 pin, rerun the whole
test matrix.

### Sprint 9 gate

- [ ] Bench top-10 ≥ 97 % with query classifier on
- [ ] `include_section_context` tool param delivers ≤ 50 ms overhead
- [ ] `trace_reference` works for at least 3 hand-curated EOS cross-refs
- [ ] RRF sweep finds a combo that dominates the default on the bench set
- [ ] `v0.2.0` tag

---

## Longer backlog (no sprint assignment yet)

- **Russian-language retrieval** — our user reads Russian docs too; extend the
  pipeline with a second embedder (multilingual-e5-large or similar) and a
  language-detection routing layer. Arctic Embed is English-only by design.
- **Multi-source ingest** — right now only arista-docs. Add Cisco, Juniper, or
  internal doc sources behind the same tool surface; namespace by `source` field
  on `documents`.
- **Distributed retrieval** — single postgres handles 10k docs fine but 100k+
  suggests sharding. Prototype pgvector's partitioning or move to a vector-DB
  fleet (Qdrant, Weaviate) with a thin compatibility shim.
- **Web UI** — "inspect this chunk's neighbours" + "show me the RRF fusion
  breakdown" help debugging retrieval quality. Blazor WASM or SvelteKit.
- **Prompt-caching for Claude clients** — when a tool result ships, return
  `ephemeral` cache hints so Claude Desktop reuses the embedding batch.
- **Benchmark regression gate in CI** — a PR that drops top-10 > 5 pp auto-
  fails; results plotted in PR comment.
- **Catalog auto-refresh** — arista-docs publishes new catalog daily; watch for
  a `catalog.json` SHA change + trigger incremental ingest.
- **Sparse retrieval tuning** — BM25's `b` and `k1` are vchord_bm25 defaults; a
  small sweep might move top-10 another 1-2 pp.
- **Answer generation mode** — optional `synthesize_answer` tool that calls a
  local small LLM (phi-4, qwen) on the retrieved chunks. Keeps the server
  sovereign (no outbound API).
- **Reranker fine-tuning** — collect (query, best_doc) pairs from real Claude
  usage, fine-tune ms-marco-MiniLM-L6-v2 on them, replace the checkpoint.

---

## Decision log for v0.2

Open questions to resolve before Sprint 7 starts:

1. **EF Core 10 path:** wait for Pgvector.EFCore upstream (Q4 2026) or fork +
   vendor? Lean: wait; our code on EF 9 works fine, no user-visible bug.
2. **FluentAssertions 8 license:** keep, migrate to Shouldly, or pin back to
   FluentAssertions 7.2.0 MIT? Lean: migrate to Shouldly in Sprint 8 as part of
   operational hygiene.
3. **GPU provider at runtime: decided — NO.** Runtime stays CPU-only. GPU is
   permitted only for offline reranker fine-tuning (Sprint 9+ backlog item).
   Latency win in Sprint 7.3 instead comes from fp16 model + query-embedding
   cache + rerank cap tuning (target p95 2.3 s → ≤ 1.2 s).
4. **Bench nDCG@10 vs binary hit rate:** binary is cheap + intuitive; nDCG
   requires labelled relevance grades (1-3 per candidate). Lean: add a
   "preferred_doc_id" field to `BenchmarkQuery` and compute MRR@10 as an
   intermediate step — cheaper than full nDCG but strictly better than binary.
