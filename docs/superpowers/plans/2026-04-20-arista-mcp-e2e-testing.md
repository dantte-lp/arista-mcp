# arista-mcp End-to-End Testing Plan

**Scope:** everything between `git clone` and "Claude Desktop answers an EVPN question via arista-mcp correctly." Covers infrastructure, ingest, retrieval, transport, and cross-platform smokes. Intended to run after every Sprint 5+ milestone and before every tagged release.

**Non-goals:** unit tests (already covered by Sprint 1–4 suites). Performance regression tests beyond the benchmark harness (separate Sprint 6+ concern).

---

## Testing pyramid

```
                         ▲
               ┌─────────┴─────────┐
               │  E2E (this plan)  │          ~6 scenarios, 20–30 min
               └─────────┬─────────┘
            ┌────────────┴────────────┐
            │  Integration (sprint gate) │    39 tests, 4 min
            └────────────┬────────────┘
     ┌──────────────────┴──────────────────┐
     │          Unit + Logic               │    ~30 tests, <200 ms
     └─────────────────────────────────────┘
```

Integration + unit layers stay green as a precondition before running E2E.

---

## Environment matrix

Each scenario runs across the matrix where meaningful:

| Axis | Values |
|---|---|
| OS | Windows 11 (WSL2 podman), Linux (native podman / docker) |
| GPU | CPU-only (default), CUDA 12 (`ARISTA_MCP__Gpu=true`) |
| Transport | stdio, Streamable HTTP |
| Reranker | OnnxReranker present, NoopReranker fallback |
| Corpus size | 1-category smoke (`--category manual`, ~225 docs), full (2426 docs) |

Matrix is collapsed pragmatically: the full matrix would be 32 runs; we execute a
**6-scenario canonical set** that hits every axis at least once.

---

## Scenario catalogue

### E2E-1: Fresh deploy, full provisioning

**Purpose:** prove a bare-metal install from clone → serving tools.

**Pre-conditions:** clean repo checkout, no existing podman volume, no `models/`.

**Steps:**
1. `podman compose -f docker/compose.yaml down -v` (idempotent reset)
2. `podman compose -f docker/compose.yaml up -d postgres` — wait for healthy
3. `psql -c "\dx"` via `podman exec` — assert 5 extensions present (vector, vchord, vchord_bm25, pg_tokenizer, pg_trgm)
4. `pwsh scripts/fetch-models.ps1` — both models downloaded, SHA-size checks pass
5. `dotnet ef database update --project src/AristaMcp.Data --startup-project src/AristaMcp.Data`
6. `psql -c "\d chunks"` — assert `bm25v` column, `idx_chunks_bm25` index, two trigger lines present

**Pass criteria:**
- Container healthy within 30 s
- Both model files ≥ expected `MinBytes`
- Two EF migrations applied (`Initial`, `AddBm25Column`)
- Full chunk schema present

**Automation:** `tests/e2e/01-fresh-deploy.sh` (Linux/WSL) + `.ps1` mirror.

---

### E2E-2: Category slice ingest (fast smoke)

**Purpose:** verify end-to-end ingest pipeline without the full-corpus wait.

**Pre-conditions:** E2E-1 passed.

**Steps:**
1. `arista-mcp ingest --category manual --verbose` — 225 docs
2. After exit, `arista-mcp ingest --category manual` again (no `--force`)
3. `psql` queries to verify:
   - `SELECT COUNT(*) FROM documents WHERE category='manual'` == 225
   - `SELECT COUNT(*) FROM chunks WHERE bm25v IS NULL` == 0
   - `SELECT status, docs_upserted, docs_skipped FROM ingest_runs ORDER BY started_at DESC LIMIT 2`
4. Modify one doc's `pdf_sha256` in the catalog (test harness sidecar), re-run
5. Verify only one doc re-ingested on step 4

**Pass criteria:**
- Step 1: status=success, docs_upserted=225, >0 chunks, all with bm25v populated
- Step 2: status=skipped (catalog SHA unchanged)
- Step 4: status=success, docs_upserted=1, docs_skipped=224
- Per-doc ingest latency p95 < 2s (surfaces regression if embed/COPY slow)

**Automation:** `tests/e2e/02-category-ingest.sh`. Runtime ~3 min.

---

### E2E-3: Full-corpus ingest + benchmark

**Purpose:** release-gate smoke over the entire catalog.

**Pre-conditions:** E2E-1 passed; `../arista-docs/data/catalog.json` present.

**Steps:**
1. `arista-mcp ingest --force` — full corpus (2426 docs)
2. Log `ingest_runs` row: docs_total, docs_upserted, chunks_upserted, duration
3. `arista-mcp bench --queries tests/fixtures/bench-queries.json --limit 10`
4. Capture the Spectre table + p50/p95/avg summary

**Pass criteria:**
- Ingest completes < **25 min** on CPU (budget includes EOS-User-Manual outlier)
- `docs_upserted == docs_total == 2426`; `docs_skipped == 0`
- `chunks_upserted ≥ 50 000` (sanity floor; expected ~65k)
- Bench `top-10 hit rate ≥ 80%` (gate built into bench command — `exit 1` otherwise)
- Bench latency p95 ≤ 500 ms per query (CPU), ≤ 150 ms (GPU)
- `SELECT COUNT(*) FROM chunks WHERE bm25v IS NULL` == 0 after ingest

**Automation:** `tests/e2e/03-full-bench.sh` — tagged manual run before each release.
Not in CI (disk + time cost). Results appended to `CHANGELOG.md` per release.

---

### E2E-4: MCP stdio transport — Claude-style client

**Purpose:** prove stdio works with a real MCP handshake, not just a local loopback.

**Pre-conditions:** E2E-2 passed (so there's data to search).

**Steps:**
1. Spawn `arista-mcp serve --transport stdio` as a subprocess with piped stdin/stdout
2. Use `ModelContextProtocol` client SDK (`McpClient.CreateAsync(StdioClientTransport)`)
   to:
   - Send `initialize` → verify server responds with protocol version + capabilities
   - Send `tools/list` → assert 5 tools returned with correct schemas
   - Send `tools/call` for `search_docs` with query "EVPN overlay" → verify ≥1 result
   - Send `tools/call` for `get_status` → verify `documents > 0`
3. Close stdin; verify server exits cleanly (exit code 0) within 2 s

**Pass criteria:**
- Handshake completes < 5 s
- All 5 tools listed
- `search_docs` returns non-empty results with `document_id`, `score`, `content`
- `get_status.documents > 0`
- Clean shutdown on stdin close (no leaked processes)

**Automation:** `tests/AristaMcp.E2E/StdioTransportE2ETest.cs` using
`ModelContextProtocol.Client` + `System.Diagnostics.Process`. ~30 s.

---

### E2E-5: MCP Streamable HTTP transport

**Purpose:** verify HTTP transport end-to-end from a real HTTP client perspective.

**Pre-conditions:** E2E-2 passed.

**Steps:**
1. Spawn `arista-mcp serve --transport http --port 8099`
2. Poll `http://127.0.0.1:8099/` until `200` on `POST` with `initialize` (max 10 s)
3. Issue POSTs:
   - `initialize` → 200, correct protocol version
   - `tools/list` → 200, 5 tools with JSON schemas
   - `tools/call` `search_docs` "MLAG configuration" → results non-empty, correct
     shape (`results[]` with `chunk_id`, `document_title`, `score`, `content`)
   - `tools/call` `lookup_section` with a `document_id` + `section_title` from step
     above → returns `{found:true, body:"..."}`
4. Send `SIGINT` (or close process); verify graceful shutdown

**Pass criteria:**
- Server ready < 10 s
- All calls return 200 with `text/event-stream` content-type
- Response bodies deserialise as valid MCP JSON-RPC 2.0
- Server logs no unhandled exceptions

**Automation:** `tests/AristaMcp.E2E/HttpTransportE2ETest.cs` via `HttpClient`. ~20 s.

---

### E2E-6: Cross-platform smoke

**Purpose:** catch WSL2/Podman-specific or Linux-specific breakage before users hit it.

**Pre-conditions:** none.

**Steps** (run on each OS):
1. Windows 11 (WSL2 podman): execute E2E-1 + E2E-2 + E2E-4 + E2E-5
2. Linux (native podman or docker): same four scenarios
3. Diff the resulting `ingest_runs` + bench rows across OSes — results must be
   deterministic (same counts, same top-1 identities for the bench set)

**Pass criteria:**
- All four subscenarios pass on both OSes
- Bench top-1 hit identities match across OSes (order doesn't matter — identities
  do, since embeddings are deterministic for fixed model + seed)
- No OS-specific code path throws

**Automation:** GitHub Actions matrix job, runs on PR to `main` and on `v*.*.*`
tag push. ~15 min/runner.

---

## Cross-cutting test utilities

- **`tests/e2e/lib/assert-psql.sh`** — wrapper that runs a `psql` query via
  `podman exec`, asserts row count or scalar value, fails with diffable output.
- **`tests/e2e/lib/wait-for.sh`** — poll a URL or `pg_isready` until ready or timeout.
- **`tests/AristaMcp.E2E/Helpers/McpClientHarness.cs`** — shared boilerplate for
  spawning the CLI + a `McpClient` + cleanly disposing both.
- **`tests/fixtures/bench-queries.json`** — already curated in v0.1.0. Extend to 50
  queries in Sprint 5 to move from smoke toward objective retrieval quality.

## CI wiring

| Trigger | Scenarios | Runner | Wall clock |
|---|---|---|---|
| Every PR | E2E-1, E2E-2, E2E-4, E2E-5 | `ubuntu-latest` + podman | ~8 min |
| Nightly `main` | Above + E2E-6 | Matrix (ubuntu, windows-latest) | ~15 min |
| `v*.*.*` tag | All, incl. E2E-3 full corpus | `ubuntu-latest` (large runner) | ~35 min |

## Quality gates

A scenario "fails" if any pass criterion is missed. A CI run blocks merge unless:
- 0 failed scenarios
- Bench hit-rate ≥ 80% (if run)
- Full build + unit + integration green (precondition; re-run in CI as first step)

Bench hit-rate slippage is surfaced as a warning-level annotation on the PR if the
drop is < 5 pp vs `main`; hard-fail at ≥ 5 pp.

## Roadmap

- **Sprint 5:** land E2E-1 through E2E-5 as scripted + the two new C# E2E tests.
  Wire into GitHub Actions.
- **Sprint 6:** land E2E-3 + E2E-6; introduce bench trending (write results to
  `tests/fixtures/bench-history.jsonl`, plot in CHANGELOG for each release).
- **Sprint 7+:** adversarial / negative scenarios — corrupted model file, malformed
  catalog, postgres disappearing mid-ingest (verify `ingest_runs.status = error`,
  not partial data).

## Open questions deferred to Sprint 5 planning

- Do we containerise `arista-mcp` itself for deployment, or keep `dotnet run` as the
  user-facing story? Answer shapes E2E-4/5 packaging.
- Do we publish as a `.NET tool` (`dotnet tool install -g arista-mcp`)? That gives a
  sixth install smoke (E2E-0: `dotnet tool install` + `arista-mcp --help`).
- Should bench evolve toward MTEB-style objective scoring (nDCG@10 on a labelled set)
  or stay at binary top-K hit-rate? MTEB requires relevance judgments we don't have.
