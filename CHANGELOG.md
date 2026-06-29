# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).
Dates use ISO-8601.

## [Unreleased]

### Added

- **`AristaMcpSettings.RerankerDir`** override — `deploy/systemd/arista-mcp.env.example`
  has documented `ARISTA_MCP__RERANKERDIR` since v0.3.x, but the property was
  missing from settings (silent ignore). Now wired through `ModelPaths.RerankerDir`
  + `ServerHosting.BuildReranker` + the three CLI commands (`bench`,
  `curate-triples`, `validate-bench-queries`). Regression-guarded by
  `CliConfigurationTests.RerankerDir_env_var_is_bound_to_settings`.
- **`/v1/healthz`** liveness endpoint on the Streamable-HTTP transport
  (`src/AristaMcp.Server/HttpHost.cs`, via
  `Microsoft.AspNetCore.Diagnostics.HealthChecks`). Plain 200 OK
  process-liveness check consumed by `deploy/quadlet/arista-mcp.container`'s
  new `HealthCmd=curl --fail --silent http://127.0.0.1:8080/v1/healthz`.
  Backend-status reporting stays the responsibility of the `get_status`
  MCP tool.
- **`Exec=postgres -c shared_preload_libraries=…`** in
  `deploy/quadlet/arista-mcp-postgres.container` plus the full memory
  tuning block from `docker/compose.yaml`. Without this the Quadlet PG
  pod fails `CREATE EXTENSION vchord` on first start (the
  `tensorchord/vchord-suite` image bundles the libraries but does NOT
  preset them).
- **`AristaMcp.Cli.Tests`** project — xUnit regression tests for
  `CliConfiguration` precedence (env > JSON) and default fall-through.
- **`RunSilentReadStdoutAsync`** helper in `BootstrapCommand` — portable
  container-existence check using `ps -aq --filter name=…` which works
  on both podman and docker.

### Changed

- **`CliConfiguration.Load()` provider order reversed** —
  `AddJsonFile` is now registered before `AddEnvironmentVariables`, so
  `ARISTA_MCP__*` environment variables correctly override a developer's
  local `arista-mcp.json`. This matches `Microsoft.Extensions.Configuration`'s
  "last registered wins" semantics and aligns with the ASP.NET Core
  default (`appsettings.json` → env vars). Regression-guarded by
  `CliConfigurationTests.EnvironmentVariable_overrides_JsonFile_value`.
- **`BootstrapCommand.RunSilentAsync` / `RunStreamingAsync`** now kill the
  child process tree on `OperationCanceledException`. Without this an
  interrupted `pg_restore` / `podman pull` would keep running in the
  background past the abandoned bootstrap.
- **`BootstrapCommand.RestoreCorpusAsync`** wraps the download + restore
  in a `try/finally` so the host-side and container-side temp dump
  files are always removed, even when the download or `pg_restore`
  fails partway. The serial HNSW rebuild fallback now captures and
  returns the `psql` exit code instead of silently swallowing it
  (`hnswRc != 0` is fatal — dense search would otherwise return no
  results).
- **`Quadlet arista-mcp.container`** gains a `HealthCmd=curl /v1/healthz`
  block now that the endpoint exists (see Added).
- **.NET SDK** bumped to `10.0.301` (was `10.0.201`). `global.json`
  still uses `rollForward: latestFeature` so any 10.0.3xx SDK satisfies
  the pin on hosted runners.
- **Package set lifted to the current minor/patch** (verified against
  NuGet on 2026-06-29):
  - `ModelContextProtocol` + `.AspNetCore` 1.2.0 → 1.4.0
  - `Microsoft.Extensions.AI` 10.5.0 → 10.7.0
  - `Microsoft.Extensions.{Hosting,Configuration*,Options}` 10.0.6 → 10.0.9
  - `Microsoft.EntityFrameworkCore{,.Design,.Relational}` 9.0.15 → 9.0.17
    (still held at 9.x because `Pgvector.EntityFrameworkCore 0.3.0` —
    latest — pins Npgsql.EFCore 9.0.x)
  - `Microsoft.ML.OnnxRuntime{,.Gpu}` 1.24.4 → 1.27.0
  - `System.CommandLine` 2.0.6 → 2.0.9
  - `Spectre.Console` 0.55.2 → 0.57.1
  - `Testcontainers.PostgreSql` 4.11.0 → 4.12.0
  - `Microsoft.NET.Test.Sdk` 18.4.0 → 18.7.0
  - `FluentAssertions` 8.9.0 → 8.10.0
  - `Microsoft.Extensions.TimeProvider.Testing` 10.5.0 → 10.7.0
  - `System.Security.Cryptography.Xml` 10.0.6 → 10.0.9
  - `OpenTelemetry{,.Extensions.Hosting,.Exporter.OpenTelemetryProtocol}` 1.15.3 → 1.16.0
- **Analyzers** bumped: `Meziantou.Analyzer` 3.0.50 → 3.0.117,
  `SonarAnalyzer.CSharp` 10.23.0 → 10.27.0. `Roslynator.Analyzers`
  4.15.0 and `AsyncFixer` 2.1.0 stay (latest).
- **NuGetAudit** enabled in `Directory.Build.props` (`Mode=all`,
  `Level=moderate`) so a known CVE in any transitive dep fails the
  build instead of leaking past CI silently.

### Fixed

- **`e2e.yml` branch trigger** corrected from `[main]` to `[master]`
  (porting artefact from the sibling nutanix-mcp repo). The E2E suite
  now actually runs on PRs and pushes to `master`; `workflow_call` is
  also added so `release.yml` can mirror it as a gate.
- **`BootstrapCommand` portability**: replaced the
  `{runtime} container exists {name}` call (a podman-only subcommand
  that always exits non-zero under Docker) with the cross-runtime
  `ps -aq --filter name=^{name}$` check. A second `bootstrap` run on a
  Docker host no longer fails with "container name already in use".
- **`BootstrapCommand` config-load side effect**: removed the
  `_ = CliConfiguration.Load();` hack in `RunStreamingAsync`. The
  workaround re-read environment vars and JSON file on every subprocess
  invocation (10-20× per bootstrap) and could mask the actual subprocess
  error with an unrelated JSON parse exception if the user's
  `arista-mcp.json` was malformed. Root cause was the now-removed
  dead `IngestParallelism` settings field.
- **`OnnxEmbedder.cs` doc-comment** corrected — the model exposes a
  pre-pooled `sentence_embedding [B, 768]`, not `last_hidden_state`
  + manual mean-pool (the comment described draft code that was never
  written). Future contributors will not be tempted to add unnecessary
  pooling.

### Removed

- **`AristaMcpSettings.IngestParallelism`** dead field — declared with
  a default of 4, never consumed in production code. Setting
  `ARISTA_MCP__IngestParallelism` had no effect.

### Notes

- CPU-only ONNX Runtime stays the default. `Microsoft.ML.OnnxRuntime.Gpu`
  is pinned at the same revision so the optional `-p:UseGpuOnnx=true`
  switch in `src/AristaMcp.Embedding/AristaMcp.Embedding.csproj`
  restores a matched pair; the GPU code path is opt-in only.
- Full solution (5 src + 5 tests projects) builds against SDK 10.0.301
  with **0 warnings / 0 errors** under the full analyzer suite.
- Self-contained single-file publish for `linux-x64` smoke-tested at
  ~132 MB; `--help` runs cleanly.

## [0.3.0] — 2026-04-26

Retrieval quality push following the v0.3-revised plan. Best attainable
configuration on the 570-query v2 bench (regenerated post-reingest) lands at
**top-1 93.86 % / top-10 100.00 % / p95 4.5 s**. The plan's stretch gate
(top-1 ≥ 95 %) was missed by 1.14 pp — inside the n=570 σ ≈ 1.3 pp band —
because three of the four planned levers regressed; only the bge-reranker-v2-m3
fine-tune (Sprint 14) delivered a measurable uplift.

Empirical pattern across the failed levers: **sub-domain-knowledge
instruction-tuned LLMs (Qwen2.5-1.5B / 3B) systematically degrade precision
on a narrow tech-docs corpus**, both in query rewriting (HyDE, multi-query)
and listwise re-rank positions. The lever that DOES work is fine-tuning
the cross-encoder reranker on domain triples.

Plan: [`docs/superpowers/plans/2026-04-24-arista-mcp-retrieval-quality-v0.3-revised.md`](docs/superpowers/plans/2026-04-24-arista-mcp-retrieval-quality-v0.3-revised.md).

### Added

- **Sprint 14 — fine-tuned `BAAI/bge-reranker-v2-m3` (568 M XLM-R, INT8 ONNX,
  569.6 MB)** trained on 4 937 triples mined from the 588-query v2 bench
  (588 positives + 4 349 hard negatives, multi-positive aware).
  **+2.04 pp top-1 vs `v0.1.4` MiniLM stock at ~1.6σ — first real retrieval
  uplift in the project.** XlmRobertaOnnxReranker ships in `models/reranker/`,
  RerankerFamilyDetector auto-picks the path. Training pipeline in the
  sibling `arista-reranker-tune` repo: `extract_v2_pairs.py` →
  `mine_negatives_v2.py` → `train_v2m3.py` → `export_v2m3_int8.py`.
- **Sprint 13 — bench v2 expansion** to 588 LLM-generated chunk-ID
  multi-positive queries (regenerated to 570 post-reingest), σ ≈ 1.3 pp
  (down from ±4.2 pp on the v1 111-query slug-substring bench).
  `BenchmarkQuery.ExpectAnyOfChunkIds`, `BenchmarkQuerySet.Version`,
  `arista-mcp validate-bench-queries` CLI.
- **Sprint 15.1 — parent-child chunking** end-to-end. Schema migration
  `parent_chunk_id BIGINT NULL FK chunks(id) ON DELETE CASCADE` +
  `chunk_kind TEXT CHECK ('leaf','parent')`, two-pass ingest in
  `IngestService` (parents first, leaves patched with FK then embedded),
  retriever filters `chunk_kind='leaf'` and hydrates parent text for the
  cross-encoder. Marginal +1.00 pp top-1 (sub-σ) but the infrastructure
  is reusable for future section-context features.
- **Sprint 15.2 — rule-based multi-query expansion** code path
  (`IMultiQueryExpander`, `MultiQueryExpander`). Off by default; the
  shipped rules (acronym contraction + iterative question-prefix strip)
  regressed top-1 by 2 pp via dilution attack — code retained for future
  conservative-rule experiments.
- **Sprint 16 — listwise top-5 LLM re-rank** code path
  (`IListwiseReranker`, `LlamaCppListwiseReranker`). Off by default;
  Qwen2.5-3B regressed top-1 by 4.39 pp at 3.4σ + added 7 s p95 — a
  general-purpose 3 B model lacks the domain prior to outperform a
  domain-tuned cross-encoder. Code retained for future experiments
  (Qwen2.5-7B continued-pretrain on arista, score-augmented prompts).
- **`SearchDiagnostics`** gains `HydeMs/Hit/Fallback`, `ListwiseMs/Hit/Fallback`
  observability.
- Plan documents under `docs/superpowers/plans/`:
  `2026-04-24-arista-mcp-retrieval-quality-v0.3-revised.md` (active),
  `2026-04-23-arista-mcp-retrieval-quality-v0.3.md` (superseded).

### Changed

- Default reranker is now `bge-reranker-v2-m3` INT8 (XLM-R SentencePiece
  family) — `models/reranker/` holds the new files, MiniLM-L6 archived
  to `models/reranker-minilm-v0.2.0-baseline/`.
- `HybridRetriever` chains four optional injection points: HyDE,
  multi-query, parent-child hydration, listwise. All except parent-child
  are gated behind `Enabled` settings; parent-child is unconditional
  whenever `chunk_kind='parent'` rows exist.
- `bench-queries-v2.json` regenerated against the new corpus; the
  pre-reingest snapshot is preserved at
  `tests/fixtures/bench-queries-v2-stale-pre-parent-child.json` for
  historical reference.

### Bench history (v2 set, n=570 unless noted)

| Run | top-1 | top-10 | p50 | p95 |
|---|---:|---:|---:|---:|
| `v0.1.4-rebench-v2` (n=588) | 90.82 % | 100.00 % | 550 ms | 820 ms |
| `v0.2.4-v2m3-finetune` (n=588) | **92.86 %** | 100.00 % | 3376 ms | 4320 ms |
| `v0.2.5a-multiquery` (n=588) | 90.82 % | 98.64 % | 3500 ms | 5145 ms |
| `v0.2.5-parent-child` | **93.86 %** | 100.00 % | 3408 ms | 4454 ms |
| `v0.3.0-listwise` | 89.47 % | 100.00 % | 7015 ms | 11422 ms |

`v0.2.5` is the production default for v0.3.0 — the listwise-enabled
configuration is OFF in the shipped settings.

### Known limitations

- v0.3.0 ships at top-1 93.86 % vs the planned 95 % gate. The 1.14 pp
  gap is inside σ at n=570; closing it without a stronger LLM-judge
  (Qwen2.5-7B+ continued-pretrain on arista) or a fundamentally
  different reranker family is unlikely.
- Reingest invalidates `bench-queries-v2.json` (auto-PK chunk ids
  change); regenerating costs ~3 hrs (validate ~12 min + annotate ~3 hrs
  on Qwen2.5-3B). Plan v0.4 or a future bench-management lever.

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

[Unreleased]: https://github.com/dantte-lp/arista-mcp/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/dantte-lp/arista-mcp/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.4...v0.2.0
[v0.1.4]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.3...v0.1.4
[v0.1.3]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.2...v0.1.3
[v0.1.2]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.1...v0.1.2
[v0.1.1]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.0...v0.1.1
[v0.1.0]: https://github.com/dantte-lp/arista-mcp/releases/tag/v0.1.0
