# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).
Dates use ISO-8601.

## [Unreleased]

Work toward v0.3.0 — lift top-1 on the 588-query v2 bench from the
90.82 % stock MiniLM baseline to ≥ 95 % while staying CPU-only.
Plan: [`docs/superpowers/plans/2026-04-24-arista-mcp-retrieval-quality-v0.3-revised.md`](docs/superpowers/plans/2026-04-24-arista-mcp-retrieval-quality-v0.3-revised.md).

_(no entries yet)_

## [0.2.0] — 2026-04-24

First post-v0.1.4 release — platform polish for **measurement** and
**query rewriting** scaffolding. Retrieval quality outcomes (HyDE,
bge-reranker-base) were neutral-to-negative, so the default stack is
unchanged; the code paths ship off-by-default for the Sprint 14 /
v0.3.0 work that will use them.

Plan: [`docs/superpowers/plans/2026-04-24-arista-mcp-release-polish-v0.2.md`](docs/superpowers/plans/2026-04-24-arista-mcp-release-polish-v0.2.md).

### Added

- **XLM-RoBERTa reranker family** — `XlmRobertaOnnxReranker` +
  `XlmRobertaRerankerTokenizer`. SentencePiece BPE tokeniser with the
  fairseq-offset remap (SP id 0 → HF 3, else sp + 1); byte-for-byte
  parity with HuggingFace `XLMRobertaTokenizer`. Enables future
  experiments with `bge-reranker-v2-m3` without touching the BERT
  WordPiece path.
- **`RerankerFamilyDetector`** auto-selects the reranker implementation
  by probing files under `models/reranker/`. `sentencepiece.bpe.model`
  triggers the XLM-R path; `vocab.txt` triggers the BERT path; neither
  falls back to `NoopReranker`.
- **HyDE query rewriting** — `IHydeExpander`, `HydeExpander`,
  `NoopHydeExpander`, `HydeSettings`. Optional local-LLM rewriter
  (llama.cpp sidecar behind `podman compose --profile llm up`) that
  feeds the dense-retrieval path only; BM25 and reranker continue to
  operate on the raw query. Off by default; opt-in via
  `ARISTA_MCP__Hyde__Enabled=true`.
- **Bench harness v2** (588 chunk-ID multi-positive queries at
  `tests/fixtures/bench-queries-v2.json`). LLM-generated queries
  stratified across 15 product families, fairness-filtered through the
  current retriever, multi-positive annotated by a second LLM pass.
- **`arista-mcp validate-bench-queries`** CLI subcommand — runs a
  candidate JSONL through `HybridRetriever` and keeps rows whose source
  chunk appears in the top-K. Used by the bench-expansion pipeline.
- **`BenchmarkQuery.ExpectAnyOfChunkIds`** and
  **`BenchmarkQuerySet.Version`** fields; v2 bench scoring is pure
  chunk-ID membership and overrides the v1 slug / title substring
  heuristic when populated.
- **`SearchDiagnostics` HyDE fields** — `HydeMs`, `HydeHit`,
  `HydeFallback`. Backwards-compatible (all default to zero / false).
- **Multilingual documentation** under `docs/en/` and `docs/ru/` with
  Mermaid architecture / retrieval / ingest diagrams, plus
  [`releasing.md`](docs/en/releasing.md).
- **`LICENSE`** (MIT), **`SECURITY.md`** (responsible-disclosure
  policy), **`.github/workflows/ci.yml`** (fast build + unit tests on
  every PR / push), **`.github/dependabot.yml`** (weekly NuGet +
  GitHub Actions scans).
- **Assembly metadata** in `Directory.Build.props` — `Version=0.2.0`,
  `Authors`, `Product`, `RepositoryUrl`, `PackageLicenseExpression`,
  `ContinuousIntegrationBuild` on CI for deterministic binaries.

### Changed

- `BenchCommand` writes `query_set_version` into every
  `bench-history.jsonl` row so v1 and v2 rows are distinguishable.
- `HybridRetriever` ctor now accepts an optional `IHydeExpander` and
  falls back to `NoopHydeExpander` when absent; pre-HyDE callers keep
  compiling unchanged.
- `fetch-models.ps1` pulls the `Qwen/Qwen2.5-1.5B-Instruct` Q4_K_M GGUF
  into `models/llm/` for the HyDE sidecar.

### Fixed

- **HyDE circuit breaker race (M1)** — `RecordFailure` now uses
  `Interlocked.CompareExchange(expected=0)` so only the first
  threshold-crosser arms the cooldown deadline; racing failures
  no-op on the ticks field. Test `CircuitArms_ExactlyOnce_UnderConcurrentFailures`
  fires 10 parallel 5xx queries and verifies the cooldown is not
  extended by skew.
- **`HydeExpander.CallLlmAsync` crash on malformed JSON (M2)** —
  `ReadFromJsonAsync` throws `JsonException`, not `HttpRequestException`,
  so a llama.cpp 200 OK with a truncated body used to propagate out
  and tear down the whole search. Now caught together with
  `NotSupportedException`; retriever falls back to the raw query.
- **`validate-bench-queries` empty-input handling (M6)** — guards
  against empty / malformed JSONL input before building the ONNX
  embedder + reranker; returns exit code 2 with a clear error instead
  of burning ~200 ms on session init.

### Research outcomes retained for provenance

- **bge-reranker-base** (v0.2.2 probe) — top-1 regressed by 3.75 pp vs
  the MiniLM baseline on the v2 bench, with 2× CPU latency. Code path
  kept for the eventual `bge-reranker-v2-m3` fine-tune; stock default
  remains MiniLM-L6-v2.
- **HyDE with Qwen2.5-1.5B / 3B** (v0.2.3 probe) — top-1 regressed by
  4.60 pp on v2 and dropped top-10 from 100 % to 96.94 %. Code ships as
  `Enabled=false` default.

## [v0.1.4] — 2026-04-23

Sprint 8 — unblock full-corpus bench + prep GPU fine-tune pipeline.

### Added

- **`arista-mcp curate-triples`** — generates `(query, positive, negatives)`
  JSONL for the Sprint 9 cross-encoder reranker fine-tune. Hard negatives
  come from top-20 HybridRetriever hits that differ in both document AND
  product from the positive; same-product negatives collapse margin loss
  to no-op. Defaults to 4 negatives per query; retrieval wiring identical
  to `bench` so `--models` and `ARISTA_MCP__` env vars work the same way.
- **`EmbeddingVariant` setting.** `fp32` (default) loads `model.onnx` as
  before; `ARISTA_MCP__EmbeddingVariant=fp16` swaps to `model_fp16.onnx`
  (~218 MB vs 436 MB, 1.5–2× CPU speedup, ≤ 1 pp nDCG@10 cost per
  Snowflake's card). `ModelPaths` helper in `Core/Settings` is the single
  resolver; every callsite (serve, bench, ingest, curate-triples) routes
  through it.
- **`IngestOptions.ChunkSubBatchSize`** (default 2000). `IngestService`
  fans embed+BulkInsert across sub-batches when a doc exceeds the
  threshold, one doc-metadata upsert + one chunk wipe up front. Motivated
  by EOS-User-Manual generating ~40 k chunks post-CRLF-fix — a single
  COPY BINARY at that scale OOMs the container.

### Changed (perf)

- **Query embedding LRU cache** in `HybridRetriever` (256 slots,
  clear-oldest-half eviction). Skips the ~100-200 ms ONNX inference on
  repeated normalised queries (common in Claude-driven flows). Eviction
  is intentionally coarse — a perf cache doesn't justify linked-list +
  lock overhead.
- **Adaptive rerank cap.** When RRF top-5 scores span ≤ 0.02, the
  candidates are effectively tied and sending 30 to the cross-encoder is
  noise; depth floors to 10 in that case. ~50 ms/query saving on
  tight-cluster searches; spread-out results still rerank the full
  `RerankTopN`.
- **Warm-on-startup.** `ServerHosting` runs a throwaway embed at DI
  resolution time so the first real `tools/call search_docs` doesn't pay
  the ~200 ms ONNX graph-init latency. Failures swallowed (non-fatal).

### Infrastructure

- **Postgres memory tuning.** `docker/compose.yaml` bumps
  `shared_buffers` 512 MB → 2 GB, `work_mem` 32 MB → 256 MB,
  `maintenance_work_mem` 2 GB → 4 GB, `max_wal_size` → 4 GB, drops
  `max_connections` 50 → 20. `init.sql` per-DB override raised to match so
  a volume wipe doesn't re-clamp to 2 GB. HNSW rebuild + bm25v trigger at
  full-corpus scale no longer OOM-kill the container.

### Observability

- **`System.Diagnostics.ActivitySource` hooks** on the retrieval + ingest
  hot paths. Source name `AristaMcp`, spans `search.hybrid` (root),
  `search.embed`, `search.dense`, `search.sparse`, `search.rerank`,
  `ingest.document`, `ingest.subbatch`.
- **OTLP exporter** via `OpenTelemetry 1.15.0` + `Extensions.Hosting`
  + `Exporter.OpenTelemetryProtocol`. Opt-in: set
  `ARISTA_MCP__Otel__Endpoint` (or OTel-spec standard
  `OTEL_EXPORTER_OTLP_ENDPOINT`) to e.g. `http://localhost:4317` and
  spans ship. When neither is set, `AddOtelIfEnabled` is a no-op — no
  DI registration, no allocations, no exporter threads.
- **Both host + CLI paths export.** `arista-mcp serve` uses the Hosting
  extension (TracerProvider lives for the app lifetime);
  `arista-mcp bench`, `ingest`, `curate-triples` use the imperative
  `OtelConfig.BuildTracerProviderIfEnabled()` with `using var` so the
  batch exporter flushes on command exit — otherwise short-lived
  commands drop the last few seconds of spans.
- **Jaeger compose** at `docker/compose.otel.yaml` + recipe in
  `docs/otel.md`. Verified end-to-end: a single `bench` run populates
  the `arista-mcp` service in Jaeger with the full span tree.

### Bench result

`v0.1.4-full-corpus-crlf` row appended to `tests/fixtures/bench-history.jsonl`
(111 queries against the full 2427-doc corpus, 59 356 chunks, GPU-assisted
ingest + query embed):

| metric | v0.1.2 | v0.1.3 (partial) | **v0.1.4** |
|---|---|---|---|
| top-1 hit rate | 70.0 % | 36.94 % | **73.87 %** |
| top-10 hit rate | 86.67 % | 67.57 % | **99.10 %** |
| p95 latency | 2342 ms | 1909 ms | **57.1 ms** (GPU) |

p95 figure is GPU-assisted — a separate CPU-only bench run is pending
for a realistic serve-time number. Retrieval-quality deltas (top-1,
top-10) hold regardless of the inference backend.

### Build variant

- **`-p:UseGpuOnnx=true` conditional OnnxRuntime package.** Default
  still resolves `Microsoft.ML.OnnxRuntime` (CPU). Opt-in to
  `.Gpu` for one-shot batch ingests via
  `pwsh scripts/build-gpu.ps1 -Release -Clean`. See `docs/onnx-provider.md`
  for rationale, gotchas (CUDA 12 DLLs from PyTorch via PATH work in a
  pinch), and the reason we can't runtime-switch (Microsoft #2198).

## [v0.1.3] — 2026-04-22

Sprint 7 — retrieval quality: heading/page-number bug hunt + bench curation.

### Fixed

- **Latent CRLF heading regex bug (Sprint 2 regression).** `.NET` regex `$` in
  Multiline mode matches only before `\n`, never at `\r`. Windows-written MD files
  with CRLF endings made every ATX heading fail the regex, so each doc silently
  collapsed into a single fallback-titled section — sectioning and page-enrichment
  never worked on the real corpus. The fix normalises `\r\n`/`\r` → `\n` before
  `HeadingRegex.Matches()`. Covered by a new `MarkdownWithCrlfLineEndings_*` test;
  prior unit tests used raw-string literals (LF-only) and missed this completely.
  Impact: per-doc page coverage jumped from 0/N to mostly-100% on real MDs
  (157/181 docs fully paged in the partial v0.1.3 ingest).
- **`StampPagesFromJson` level mismatch.** `arista-docs.enrich.build_sections`
  flattens every TOC entry to `level=1`, while the MD walker reads real
  `#`/`##`/`###` depth. The old `(level, title)` key pairing dropped ~28 % of
  chunks to null pages even when titles matched perfectly. New algorithm pairs by
  cleaned title order with a lookahead-3 window; levels are ignored.

### Added

- **`BenchmarkQuery.expect_product`** field — exact match on `ChunkResult.Product`.
  Products like `hardware` / `aboot` / `cva` / `cvw` use model-number slugs
  (`7050X3-Datasheet`, `21630-aboot-measured-boot-6-0-0`) that no substring token
  in slug/title can catch. The four Sprint 5 bench misses ship as retrieval wins,
  not curation bugs.
- **Bench set expanded 30 → 111 queries** spread across the catalog's product
  distribution (eos 23, cvp 12, dmf 8, hardware 8, cv-cue 6, cvw 6, cva 3, mss 3,
  cloudeos 3, aboot 2, velocloud 2, avd/campus/analytics 1 each, plus 32 general
  / cross-product). 3-point-percentage regressions are now detectable.
- **`search_docs --dedup-per-section` flag.** When set, drops lower-scored
  duplicate chunks from the same `(document_id, section_title)` post-rerank,
  yielding more diverse top-K when a long section dominates dense retrieval.
- **AsyncFixer 1.6.0 → 2.1.0** (Sprint 7.5 early) — no new warnings surfaced;
  zero suppressions required across `src/`.

### Bench history

- `v0.1.3-crlf-fix-181docs-partial`: 111 queries, top-1 36.9 %, top-10 67.6 %, p50
  1394 ms, p95 1909 ms. Partial ingest — EOS-User-Manual's ~17k sections now
  generate ~3× more chunks post-CRLF-fix, pushing postgres past our current
  resource envelope. **Full-corpus re-bench deferred to Sprint 8** which will
  tune memory (shared_buffers / work_mem) + possibly chunk EOS as a second-pass.

### Deferred to Sprint 8

- Full-corpus ingest under expanded chunk scale (~30–40 k chunks vs the old 9 569).
- `bench --history` plotting / regression gate in CI.
- CPU-only latency optimisations (fp16 embedder, query LRU, adaptive rerank cap,
  warm-on-startup) — same target as original Sprint 7.3: p95 ≤ 1.2 s.

## [v0.1.2] — 2026-04-21

Sprint 6 — test-DB isolation + full-corpus baseline.

### Added

- **Full-corpus bench baseline.** Ingested all 2426 arista-docs documents (9569 chunks,
  24:31 wall-clock on CPU) into the prod `arista` DB; bench result appended to
  `tests/fixtures/bench-history.jsonl` as `v0.1.2-full-corpus-baseline`:
  - top-1 hit rate **70.0%** (+3.3 pp vs v0.1.1 manual-only)
  - top-10 hit rate **86.7%** (+6.7 pp vs v0.1.1 manual-only)
  - latency p50 1812 ms / p95 2342 ms / avg 1807 ms (CPU ONNX, 12-core)
- **Sprint 6 plan doc** at `docs/superpowers/plans/2026-04-20-arista-mcp-sprint-6.md`.

### Changed

- **`PgvectorFixture` now provisions its own `arista_test` database.** Default
  connection string changed to `Database=arista_test`; on `InitializeAsync` the
  fixture connects to the maintenance `postgres` DB, creates the test DB if missing,
  applies `docker/init.sql` extensions, runs EF migrations, then truncates. Prod
  `arista` ingest is never touched by `dotnet test` again.
- **Safety guard:** the fixture refuses to run against any DB whose name doesn't end
  in `_test` (override via `ARISTA_MCP_TEST_CS` for unusual setups). Prevents the
  exact "release-gate wipes the bench dataset" incident that Sprint 5 hit.

### Notes

- Earlier fear of a "5-hour CPU ingest" was conservative. Actual budget on a 12-core
  Windows/Podman host: **~25 minutes** for the full 2426-doc corpus. EOS-User-Manual
  (5234 pages / 17k sections) dominates the first ~20 min; the remaining 2425 docs
  finish in the last 5.
- Four bench queries still miss (`hardware`, `aboot`, `CVA`, `CVW`) but that's a
  bench-set curation issue — the query's `expect_any` tokens don't match the actual
  slug conventions in the catalog, not a retrieval quality problem.

## [v0.1.1] — 2026-04-20

Sprint 5 — post-v0.1 hygiene + JSON enrichment + E2E coverage.

### Added

- **Page numbers in `search_docs` output.** `DocumentLoader` now reads the per-doc
  `{slug}.json` alongside the MD file and stamps `page_start`/`page_end` onto each
  section by pairing MD headings against JSON sections in order, keyed by
  `(level, cleaned_title)`. Heading normaliser ports `arista-docs.enrich._clean_heading`
  (strips `**bold**`, `*italic*`, `_underscore_`, inline HTML, collapses whitespace).
  Missing/corrupt JSON falls back to MD-only (null pages).
- **`FakeTimeProvider` in tests** — `Microsoft.Extensions.TimeProvider.Testing 10.5.0`.
  Two regression tests on `IngestRunRepository` assert exact-offset `FinishedAt -
  StartedAt` across `Advance(5m)` and multiple accumulating advances.
- **`bench --history` / `--label` flags.** Appends one JSONL row per run (date, label,
  query_count, top_k, top1 & topk hit rates, p50/p95/avg latency). First baseline row:
  `v0.1.1-manual-category-baseline` on 225 docs / 7347 chunks: top-1 66.7%, top-10
  80.0%, p50 1638 ms, p95 2127 ms (CPU embedder, Windows/Podman).
- **E2E test surface** — `tests/AristaMcp.E2E/`:
  - `StdioTransportE2ETest` spawns `arista-mcp serve --transport stdio`, does raw
    JSON-RPC initialize + `tools/list`, asserts all 5 tools present (~540 ms).
  - `HttpTransportE2ETest` spawns `--transport http` on ephemeral port, `HttpClient`
    POST with SSE body parse, same assertions (~970 ms).
  - `tests/e2e/01-fresh-deploy.sh` + `02-category-ingest.sh` — shell scenarios for
    schema provisioning + category-slice ingest + incremental skip.
- **GitHub Actions E2E workflow** (`.github/workflows/e2e.yml`) on ubuntu-latest with a
  service-container postgres, NuGet + model caching, full build + unit/integration +
  E2E. SkippableFacts gracefully skip on empty corpus.

### Changed

- **AsyncFixer 1.6.0 → 2.1.0.** No rule-ID renames; strictly fewer false positives on
  `AsyncFixer02`. Zero source-level suppressions required.

### Fixed

- **`MissingMethodException` on direct `dotnet run`.** `Pgvector.EntityFrameworkCore
  0.3.0` pulls `Microsoft.EntityFrameworkCore.Relational 9.0.1` transitively, which was
  copied into the publish output while our code targeted 9.0.15 (NU1608 warning
  promoted to a runtime failure for the CLI). Fixed with a direct
  `Microsoft.EntityFrameworkCore.Relational 9.0.15` pin in `AristaMcp.Data`.

### Quality gate

48/48 tests green (21 Core + 3 Embedding + 17 Data + 2 E2E + 5 new enrichment tests
+ 2 FakeTimeProvider tests). `dotnet build` clean; all 10 projects.

## [v0.1.0] — 2026-04-20

Initial release. Four waterfall sprints from spec to tag.

### Added — Sprint 1 (infrastructure + data layer, tag `sprint-1-review`)

- .NET 10 solution skeleton with Central Package Management, Directory.Build.props-level
  analyzers (Meziantou, Roslynator, SonarAnalyzer, AsyncFixer, BannedApiAnalyzers).
- Docker/Podman postgres 18 image on `tensorchord/vchord-suite:pg18-latest` with pgvector,
  vchord, vchord_bm25, pg_tokenizer, pg_trgm pre-loaded; `shared_preload_libraries` wired
  for `vector,vchord,vchord_bm25,pg_tokenizer` so `ALTER DATABASE SET hnsw.*` works.
- Core models (`AristaDocument`, `AristaChunk`, `ChunkResult`, `SearchResponse`,
  `SearchDiagnostics`), settings class, `McpTransport` enum.
- EF Core 9.x + Npgsql.EFCore 9.x + Pgvector.EFCore 0.3.0 data layer. Initial migration
  provisions documents / chunks / ingest_runs with `halfvec(768)` embeddings and HNSW
  `halfvec_cosine_ops` index (m=16, ef_construction=200).
- `DocumentRepository` (upsert/get/delete) + `ChunkRepository` with Npgsql `COPY BINARY`
  bulk insert (1000 rows < 3 s).
- Testcontainers pivoted to local shared postgres on Windows/Podman (race with two-stage
  init). Three integration fixtures: HalfVector round-trip, HNSW cosine nearest neighbour,
  BM25 `<&>` relevance ordering.

### Added — Sprint 2 (embedding + ingest, tag `sprint-2-review`)

- PowerShell model fetcher (`scripts/fetch-models.ps1`) with idempotent size checks.
- `OnnxEmbedder` targeting snowflake-arctic-embed-m-v1.5: reads the pre-pooled
  `sentence_embedding` output directly, defensively re-normalizes via span copy.
- `BertWordPieceTokenizer` wraps `Microsoft.ML.Tokenizers.BertTokenizer` with a batch
  encode that pads to max-in-batch and builds the attention mask.
- `SectionAwareChunker`: section-boundary splits (target 512, max 1200, overlap 64, min
  40 tokens). Content gets `"{doc.title} > {section.title}\n\n"` prefix for embedding;
  `raw_content` unprefixed for BM25 + display.
- `CatalogReader` + `DocumentLoader` — arista-docs v0.1.x catalog contract, markdown
  split by ATX headings (named-group `[GeneratedRegex]` with `ExplicitCapture`).
- `IngestService` orchestrator with two-layer incremental skip (catalog SHA256 + per-doc
  pdf_sha256). `IngestRunRepository` tracks every run.
- `arista-mcp ingest` CLI verb via `System.CommandLine 2.0.6` (`--catalog`, `--force`,
  `--dry-run`, `--category`, `--verbose`, `--models`).

### Added — Sprint 3 (retrieval + MCP server, tag `sprint-3-review`)

- `QueryExpander` with 20 Arista acronyms (EVPN/VXLAN/MLAG/BGP/OSPF/LACP/sFlow/SR/MSS/
  AVD/LANZ/VARP/VRRP/VRF/QoS/ACL/TCAM/EOS/CVP/DMF).
- `HybridRetriever` with parallel dense (`embedding <=> $1::halfvec`) + sparse
  (`bm25v <&> to_bm25query(...)`) SQL, Reciprocal Rank Fusion (k=60), optional rerank.
- Five MCP tools (`search_docs`, `lookup_section`, `list_documents`, `get_document`,
  `get_status`) as `[McpServerToolType]` classes with constructor DI.
- `StdioHost` (console `Host`, logs to stderr) and `HttpHost` (`WebApplication` + `MapMcp`,
  stateless) backed by shared DI in `ServerHosting.AddAristaMcpServices`.
- `arista-mcp serve --transport stdio|http --port <n>` verb. Verified end-to-end: MCP
  Streamable HTTP `tools/list` returns all five tools with JSON schemas.

### Added — Sprint 4 (polish + release, tag `v0.1.0`)

- `OnnxReranker` — BERT cross-encoder (ms-marco-MiniLM-L6-v2, ~91 MB). Pair-encodes
  `[CLS] query [SEP] doc [SEP]` with proper `token_type_ids`. Falls back to `NoopReranker`
  when `models/reranker/` is absent.
- `TimeProvider` injected into `DocumentRepository` + `IngestRunRepository`; DI registers
  `TimeProvider.System` as singleton.
- `arista-mcp bench` — 30-query curated retrieval harness, Spectre-rendered per-query table
  + p50/p95/avg latency summary; exits non-zero if top-10 hit rate < 80%.
- User-facing `README.md` with quickstart, Claude Desktop config snippet, tool list,
  architecture diagram, Windows/Podman WSL port notes.
- `CHANGELOG.md` (this file).

### Fixed — Sprint 4 (three bugs surfaced by the Sprint 3 code review)

- **`Bm25Score` on dense+sparse co-hits** — RRF accumulator now tracks `DenseDistance`
  and `SparseDistance` separately; `Build()` derives `DenseSimilarity = 1 - dense_distance`
  and `Bm25Score = -sparse_distance` (sign-flipped to "higher = better" convention).
  Previously co-hit chunks returned `-cosine_distance` as the BM25 score.
- **`DenseQueryMs` / `SparseQueryMs` hard-coded to 0** — `RunDenseAsync` / `RunSparseAsync`
  now return `(rows, elapsed_ms)` via inner `Stopwatch`es; `SearchDiagnostics` reports
  real per-stage timings.
- **Manual `bm25v` SQL never auto-applied** — moved into a proper
  `AddBm25Column` EF migration with `migrationBuilder.Sql`; `Migrations/Manual/` folder
  deleted. A fresh `dotnet ef database update` now provisions the full schema.

### Changed — Sprint 4 (C# 14 / .NET 10 modernizations)

- `QueryExpander.Synonyms` → `FrozenDictionary<string, string>` (read-mostly lookup).
- `HybridRetriever.ReciprocalRankFusion` uses `CollectionsMarshal.GetValueRefOrAddDefault`
  (one hash probe per row instead of two).
- `OnnxEmbedder.ExtractAndNormalize` replaces scalar sum-of-squares loop with
  `TensorPrimitives.Norm` + `TensorPrimitives.Divide` (SIMD).
- `ChunkRepository.BulkInsertAsync` and `HybridRetriever.SearchAsync` replace manual
  `Half[]` populate loops with `[.. src.Select(static f => (Half)f)]` collection
  expressions.

### Chore — Sprint 4

- Cleanup: removed `_ = jsonFull` dead code in `DocumentLoader`; sealed entity classes and
  repositories; `GetDocumentTool` returns tags as `string[]` (not raw JSON).
- Safe patch bumps: Spectre.Console 0.55.0 → 0.55.2, Meziantou.Analyzer 3.0.48 → 3.0.50,
  System.Security.Cryptography.Xml 9.0.15 → 10.0.6.

### Known limitations

- **EF Core pinned to 9.x** — `Pgvector.EntityFrameworkCore 0.3.0` (the only release)
  targets EF Core 9.x via `net8.0` TFM. Bump blocked until upstream publishes an EF Core
  10 build. Everything else is on latest stable.
- **FluentAssertions 8.x** — non-commercial license. Solo / personal use is fine; a
  commercial deployment should either migrate to Shouldly or pin back to
  FluentAssertions 7.2.0 (last Apache-2.0).
- **Benchmark harness ships, but `bench` hit-rate numbers are indicative** — the 30-query
  set was hand-curated; a larger held-out set would be needed before claiming an objective
  quality number.
- **Reranker downloads an additional ~91 MB** — if `models/reranker/` is empty,
  `NoopReranker` (passthrough) is used automatically. Quality is noticeably better with
  the reranker.

### Stack summary

.NET 10 SDK 10.0.201 · TFM `net10.0` · ModelContextProtocol 1.2.0 · EF Core 9.0.15 ·
Npgsql.EFCore 9.0.4 · Pgvector.EFCore 0.3.0 · Microsoft.ML.OnnxRuntime 1.24.4 ·
Microsoft.ML.Tokenizers 2.0.0 · System.CommandLine 2.0.6 · Spectre.Console 0.55.2 ·
xUnit 2.9.3 · FluentAssertions 8.9.0 · PostgreSQL 18 + pgvector 0.8.2 + vchord 1.1.1 +
vchord_bm25 0.3.0 + pg_tokenizer 0.1.1.

---

[Unreleased]: https://github.com/dantte-lp/arista-mcp/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.4...v0.2.0
[v0.1.4]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.3...v0.1.4
[v0.1.3]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.2...v0.1.3
[v0.1.2]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.1...v0.1.2
[v0.1.1]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.0...v0.1.1
[v0.1.0]: https://github.com/dantte-lp/arista-mcp/releases/tag/v0.1.0
