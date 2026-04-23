# arista-mcp Sprint 8 Implementation Plan — Unblock Quality Measurement

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or executing-plans.

**Goal:** Unblock everything that's currently gated on the post-CRLF-fix corpus
size. Tune postgres + EOS-User-Manual strategy so full ingest finishes. Drop
CPU latency to interactive range. Prepare the data curation pipeline for
Sprint 9 reranker fine-tuning (GPU train → CPU serve).

**Reference:**
- Research + v0.1.3 CHANGELOG notes (session 2026-04-22).
- Sprint 7 (v0.1.3) deferred items: CPU latency opts, full-corpus baseline.
- New upstream task (arista-docs): Supported Features source adapter
  (`../arista-docs/docs/superpowers/plans/2026-04-23-supported-features-source.md`).

---

## Sprint 8 Overview

| # | Task | Prio | Gates |
|---|------|------|-------|
| 8.0 | Supported Features parser (arista-docs repo) | ⭐ independent | no arista-mcp change needed; triggers a fresh catalog |
| 8.1 | Postgres memory tuning for 30-40 k chunk scale | 🔴 blocker | shared_buffers + work_mem + maintenance_work_mem; ingest no longer OOMs |
| 8.2 | EOS-User-Manual split strategy | 🔴 blocker | the single-biggest-doc outlier fits in DB resource envelope |
| 8.3 | Full-corpus re-ingest + re-bench | 🟡 metric | v0.1.3-real baseline row in bench-history |
| 8.4 | CPU latency optimisations (deferred 7.3) | 🟡 quality | p95 ≤ 1.2 s on CPU |
| 8.5 | Triples curation CLI (prep for Sprint 9) | 🟢 data | `arista-mcp curate-triples` writes `triples.jsonl` ready for GPU fine-tune |
| 8.6 | Structured logging + OTEL (original 8.x) | 🟢 ops | traces visible in Jaeger, zero-alloc LoggerMessage |
| 8.7 | Sprint gate + `v0.1.4` tag | 🏁 release | CHANGELOG + CLAUDE.md + tag |

**Definition of Done:**
- [ ] Full 2426-doc ingest completes on the Sprint 8 postgres config, no OOM.
- [ ] Fresh bench row in `bench-history.jsonl` labelled `v0.1.4-full-corpus-crlf`.
- [ ] `arista-mcp bench` p95 ≤ 1.2 s CPU on the full-corpus DB.
- [ ] `arista-mcp curate-triples` exports ≥ 500 usable triples from the 111-query set.
- [ ] OTEL traces land in a local Jaeger container from `arista-mcp serve`.
- [ ] `v0.1.4` tag exists; CHANGELOG + CLAUDE.md updated.

---

## Task 8.0: Supported Features parser (pre-requisite in upstream repo)

**Repository:** `../arista-docs` (Python). See the dedicated plan at
`docs/superpowers/plans/2026-04-23-supported-features-source.md` in that repo.

This is strictly independent of arista-mcp: once the new catalog ships a
`supported-features` doc with `category="reference"`, a simple
`arista-mcp ingest --force` re-reads the updated catalog and the doc flows in.
No arista-mcp code change. Landing order: 8.0 first (runs in ~1 day), then
resume 8.1 so the postgres tuning covers the new content.

**Exit criteria for 8.0 in this sprint's context:**
- `../arista-docs/data/catalog.json` contains one new entry `supported-features`
  with non-empty MD + enriched JSON.

---

## Task 8.1: Postgres memory tuning

**Files:**
- Modify: `docker/compose.yaml` — `command:` args for postgres.

**Problem:** Current `command: postgres -c shared_buffers=1GB …` is sized for the
pre-CRLF-fix corpus (~9.6 k chunks). Post-fix each doc produces 3-5× more chunks;
EOS-User-Manual alone generates ~40 k chunks. HNSW index rebuild and bm25v
trigger work push the container into OOM-kill territory.

**Steps:**

1. Measure current memory ceiling:

    podman stats arista-mcp-postgres --no-stream

   Confirm the container cap (`--memory` in compose.yaml). Default is host RAM /
   unlimited, but WSL2 clamps to `.wslconfig` — verify that too.

2. Write the new postgres config in `docker/compose.yaml`:

    command: >-
      postgres
      -c shared_buffers=2GB
      -c work_mem=256MB
      -c maintenance_work_mem=4GB
      -c max_wal_size=4GB
      -c effective_cache_size=8GB
      -c shared_preload_libraries=vector,vchord,vchord_bm25,pg_tokenizer,pg_trgm
      -c max_connections=20

   Reasoning:
   - `shared_buffers=2GB` — holds 40 k `halfvec(768)` rows (~120 MB) easily +
     HNSW internal pages + bm25 indexes.
   - `work_mem=256MB` — BM25 trigger's tokenization needs this for long chunks.
   - `maintenance_work_mem=4GB` — HNSW rebuild uses this; currently the biggest
     memory spike during ingest.
   - `max_wal_size=4GB` — reduces checkpoint thrash during bulk COPY.
   - `effective_cache_size=8GB` — planner hint; not allocated.
   - `max_connections=20` — we use ≤ 4; lower cap frees shared memory.

3. `podman compose down -v && podman compose up -d postgres`. Wait for
   healthcheck. Confirm via `SHOW shared_buffers;` and `SHOW work_mem;`.

4. Regression test: `dotnet test` full suite against the `arista_test` DB —
   must stay green.

**Commit:** `ops(docker): postgres memory tuning for post-CRLF-fix chunk scale`.

## Task 8.2: EOS-User-Manual split strategy

**Files:**
- Modify: `src/AristaMcp.Cli/Ingest/IngestService.cs` — break the per-doc
  atomic transaction into per-section sub-batches when `chunk_count_estimate`
  > `ChunkBatchCeiling`.

**Problem:** Today `IngestService` processes each doc as one atomic unit:
chunker → embedder → `BulkInsertAsync` all-or-nothing. EOS-User-Manual's 17 k
sections produce ~40 k chunks in a single `COPY BINARY` call, breaking the
Npgsql buffer + postgres receive pipeline when the accumulated `bm25v` trigger
work spikes.

**Solution:** per-doc, sub-batch when the chunker returns more than `N`
sections (default 2000). Each sub-batch commits independently to `chunks`;
documents/ingest_runs updates happen once at the end. Crash-safe: a partial
EOS ingest can resume with the same pdf_sha256 skip logic.

**Pseudocode:**

    // in IngestService.IngestSingleDocumentAsync
    var drafts = _chunker.Chunk(loaded).ToList();
    const int SubBatchSize = 2000;
    var subBatches = drafts.Chunk(SubBatchSize);

    foreach (var subBatch in subBatches) {
        var vectors = await _embedder.EmbedAsync(subBatch.Select(d => d.Content).ToList(), ct);
        var entities = BuildEntities(loaded.Id, subBatch, vectors);
        await _chunks.BulkInsertAsync(entities, ct);
        progress.LogSubBatch(loaded.Slug, subBatch.Count);
    }
    await _documents.UpsertAsync(loaded.Metadata, ct);
    await _runs.RecordDocIngestedAsync(runId, loaded.Id, drafts.Count, ct);

**Test:** new fixture `big-fake-doc.md` with 5 000 synthetic sections; ingest
under `ChunkBatchCeiling=2000` must commit in 3 sub-batches and leave the DB
consistent on a mid-batch exception (negative test).

**Commit:** `feat(ingest): per-doc sub-batching for huge outliers (EOS scale)`.

## Task 8.3: Full-corpus re-ingest + re-bench

**Steps:**

1. Fresh `arista` DB via `podman compose down -v && up -d`.
2. `dotnet ef database update --project src/AristaMcp.Data --startup-project src/AristaMcp.Data`.
3. `arista-mcp ingest --catalog ../arista-docs/data/catalog.json --force --verbose`
   in background. Expect **40-60 min wall** given the sub-batched EOS path and
   bumped memory.
4. `arista-mcp bench --queries tests/fixtures/bench-queries.json --limit 10
   --history tests/fixtures/bench-history.jsonl --label v0.1.4-full-corpus-crlf`.
5. Capture:
   - Chunk count (expect 30-45 k).
   - % docs fully paged (expect ≥ 95 %).
   - Top-1/Top-10 hit rate.
   - p50/p95 latency.

**Gate:** top-10 ≥ 90 % (vs v0.1.2's 86.7 % on pre-CRLF-fix corpus); p95 ≤ 2 s.

**Commit:** `docs: v0.1.4 full-corpus baseline row` after the bench completes.

## Task 8.4: CPU-only latency optimisations

**Files:**
- `src/AristaMcp.Embedding/EmbeddingOptions.cs` — add `ModelVariant` enum
  (fp32 / fp16).
- `src/AristaMcp.Embedding/OnnxEmbedder.cs` — load the fp16 model when configured.
- `scripts/fetch-models.ps1` — pull `model_fp16.onnx` as an optional variant.
- `src/AristaMcp.Server/Retrieval/HybridRetriever.cs` — query LRU cache +
  adaptive rerank cap.
- `src/AristaMcp.Server/ServerHosting.cs` — call a warm-up embedder run at
  host startup.

**Four wins, each measurable independently:**

### 8.4a fp16 embedder variant (1.5-2× CPU speedup)

Snowflake ships `onnx/model_fp16.onnx` (~218 MB vs 436 MB fp32). The loss in
nDCG@10 is sub-1 pp per their card. Swap via
`ARISTA_MCP__EmbeddingModelVariant=fp16` — default stays fp32 to preserve
current quality floor; users opt in for latency.

### 8.4b Query-embedding LRU cache

    // in HybridRetriever
    private readonly LruCache<string, HalfVector> _queryCache = new(capacity: 256);

    public async Task<SearchResponse> SearchAsync(...) {
        var normalised = _expander.Expand(query).Normalised;
        if (!_queryCache.TryGet(normalised, out var qVec)) {
            qVec = (await _embedder.EmbedAsync([normalised], true, ct))[0].AsHalfVector();
            _queryCache.Add(normalised, qVec);
        }
        …
    }

Wins on conversational flows: Claude often issues `"EVPN"` then
`"EVPN configuration"` — query expansion normalises enough that the second
benefits from the first's cached vector if identical, else misses.

### 8.4c Adaptive rerank cap

Instead of always reranking 30 post-RRF candidates, only rerank when RRF
top-5 scores span > threshold (indicating spread among candidates, not a tight
cluster where rerank is noise). Cluster-tight → rerank top-10 only.

    var topFive = fused.Take(5).ToArray();
    var spread = topFive[0].RrfScore - topFive[^1].RrfScore;
    var rerankTopN = spread > 0.02f ? options.RerankTopN : 10;

Saves ~50 ms per query when candidates cluster.

### 8.4d Warm-on-startup

Add one throwaway `EmbedAsync(["warmup"], isQuery: false, ...)` call during
`ServerHosting.AddAristaMcpServices` when the `IEmbedder` is first constructed.
First real request no longer pays the ~200 ms graph-init cost.

**Measurement:** re-bench with each win individually enabled. Commit per win
with the delta in the commit message.

**Commits (one per):**
- `perf(embedder): fp16 model variant (1.5-2× CPU speedup, -0.5 pp nDCG)`
- `perf(retriever): LRU cache for query embeddings`
- `perf(retriever): adaptive rerank cap when RRF cluster tight`
- `perf(server): warm-up embedder session on host startup`

## Task 8.5: Triples curation CLI

**Files:**
- `src/AristaMcp.Cli/Commands/CurateTriplesCommand.cs`
- `src/AristaMcp.Cli/Curation/TripleWriter.cs`

**Usage:**

    arista-mcp curate-triples \
      --queries tests/fixtures/bench-queries.json \
      --out tests/fixtures/reranker-triples.jsonl \
      --negatives-per-query 4

**Algorithm:**
1. Load 111 bench queries.
2. For each, run `HybridRetriever.SearchAsync(query, Limit: 20, DedupPerSection: true)`.
3. Positive = top-1 that also matches `expect_product` (or `expect_any`).
4. Hard negatives = top-ranked docs from OTHER products than the positive's
   product. Take 4 per query.
5. Write JSONL:

        {"query": "…", "positive": {"doc_id": "…", "chunk_id": 123, "text": "…"},
         "negatives": [{"doc_id": "…", "chunk_id": 456, "text": "…"}, …]}

**Output:** ~440 triples (111 queries × 4 negatives average), more than Sprint
9's 500-triple floor once we include automatic augmentation (paraphrases).

**Test:** unit test with a stub `IHybridRetriever` that returns canned
results; assert JSONL shape and that positive != any negative.

**Commit:** `feat(cli): curate-triples — generate (query, positive, negatives) JSONL for reranker fine-tune`.

## Task 8.6: Structured logging + OTEL

**Files:**
- `Directory.Packages.props` — add
  `Microsoft.Extensions.Logging.Abstractions`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`,
  `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`.
- `src/AristaMcp.Server/Observability/OtelConfig.cs` — register the sources.
- `src/AristaMcp.Server/Retrieval/HybridRetriever.cs` — `[LoggerMessage]` partial
  methods for per-query structured log; add `Activity` span around dense/sparse
  branches.
- `src/AristaMcp.Cli/Ingest/IngestService.cs` — `[LoggerMessage]` per doc.

**Metrics (histograms + counters):**
- `arista_mcp.search.duration_ms` (tags: has_product, has_category)
- `arista_mcp.search.dense_hits` / `sparse_hits`
- `arista_mcp.ingest.chunks_per_second` (gauge)
- `arista_mcp.rerank.score_top1` (histogram of top-1 reranker scores)

**Opt-in:** enable only when `ARISTA_MCP__Otel__Endpoint` is set. README
section with Jaeger docker-compose one-liner.

**Commit:** `feat(server): structured logging + OTEL traces/metrics (opt-in via env)`.

## Task 8.7: Sprint gate + v0.1.4

- Full `dotnet test` green on `arista_test`.
- `arista-mcp bench` p95 ≤ 1.2 s on post-optimisation full-corpus DB.
- `arista-mcp curate-triples` produces ≥ 500 rows for a ≥ 90 %-top-10 bench.
- Jaeger spans visible for a single `tools/call search_docs` invocation.
- CHANGELOG.md `[v0.1.4]` section written.
- CLAUDE.md Sprint 8 additions block added.
- `git tag v0.1.4`.

---

## Sprint 9 preview (v0.2.0) — reranker fine-tune on GPU, serve on CPU

Unblocked by Sprint 8's triples output + full-corpus baseline:

1. **Python training repo** `arista-reranker-tune` (separate repo): `pyproject.toml`,
   `train.py`, `eval.py`, HF Transformers + sentence-transformers.
2. **Training loop** — cross-encoder margin loss on triples. RTX 4070, batch 32,
   3-5 epochs, ~1-2 h wall.
3. **Eval split** — hold out 20 triples; target MRR@10 uplift ≥ 0.05.
4. **Export** — `optimum-cli export onnx --model ./tuned --task text-classification`.
5. **Drop-in replacement** — copy ONNX + `vocab.txt` to `arista-mcp/models/reranker/`.
   `OnnxReranker` unchanged; CPU runtime picks up the new weights.
6. **Bench** — `--label v0.2.0-finetuned-reranker`; gate: top-10 ≥ 95 %, top-1 +5 pp.
7. **Tag** `v0.2.0`.

Risks: tuned model may overfit to the 111-query bench set. Mitigation: augment
triples with in-domain paraphrases (ask Claude to rewrite queries, cheap);
split eval cleanly.
