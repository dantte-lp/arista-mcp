# arista-mcp Retrieval Quality v0.3 — HyDE + v2-m3 INT8 + Parent-Child Chunking

> **SUPERSEDED on 2026-04-24** by `2026-04-24-arista-mcp-retrieval-quality-v0.3-revised.md`.
> Sprint 10 (HyDE) shipped and failed its gate (top-1 72.07 %, p95 3646 ms).
> The bench run surfaced a measurement-floor finding — 5 independent levers
> converged to top-1 72–74 % / top-10 99.10 %, with the entire top-1 variance
> attributable to ~2 queries flipping between rank 1 and rank 2–10. At n=111
> and p ≈ 0.73 the binomial σ is ±4.2 pp, so Sprint 11/12 gates ("+1 pp over
> previous") were inside the noise floor from the start. The revised plan
> front-loads **bench expansion** (111 → 400+ queries with rigorous ground
> truth) as a prerequisite before the remaining retrieval-quality work.
> This document is kept for its HyDE implementation notes and risk table.

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or executing-plans.

**Overall goal:** ship `v0.3.0` with **top-1 ≥ 78 %** on the 111-query bench
(+4.13 pp vs the `v0.1.4-full-corpus-crlf` baseline 73.87 %). CPU-only serve,
p95 latency ≤ 3 s.

**Why split into three sprints:** every past single-lever attempt (Sprint 9
MiniLM fine-tune, v0.2.1 hard-neg, v0.2.2 bge-base swap) moved top-1 by
less than one binomial σ (≈ 4.2 pp at n=111, p≈0.73). Stacking three
independent levers, each benched in isolation, is how we see which one(s)
actually move the needle — a compound improvement without attribution is
worthless for future direction.

**Current state (2026-04-23, post-v0.2.2 revert):**
- top-10 hit rate: **99.10 %** (retrieval ceiling).
- top-1 hit rate: **73.87 %** (reranker cannot break ties among the ~5
  close candidates that make the top-10).
- p50 latency: ~500 ms with stock MiniLM reranker.
- Default assets: `models/reranker/` = MiniLM-L6 WordPiece.

## Why these three levers

| Lever | Mechanism | Expected uplift | Risk |
|---|---|---|---|
| **HyDE** (Sprint 10) | LLM rewrites query into a hypothetical answer paragraph before dense embedding — retrieval latches onto richer semantic surface | +2 to +4 pp ([Haystack/HyPE benches show +5–45 pp on general corpora; tech-docs typically lower end](https://docs.haystack.deepset.ai/docs/hypothetical-document-embeddings-hyde)) | Latency +300–800 ms (CPU LLM); quality regression if LLM hallucinates off-domain |
| **bge-reranker-v2-m3 INT8** (Sprint 11) | 568 M param SOTA reranker ([BAAI/bge-reranker-v2-m3 card](https://huggingface.co/BAAI/bge-reranker-v2-m3)); INT8 keeps p50 ≤ 2 s on CPU | +1 to +3 pp (Nutanix hit MRR 0.841 with this model on similar corpus) | Quantization accuracy drop (usually < 1 pp); CPU latency drift |
| **Parent-child chunking** (Sprint 12) | Dense retrieves on small (512-tok) leaf chunks; reranker scores the full **parent section** (up to 2 k tokens) for richer tie-breaking context | +1 to +3 pp ([LangChain ParentDocumentRetriever canonical method](https://python.langchain.com/docs/how_to/parent_document_retriever/)) | Schema migration + full reingest; breaks API if response shape changes |

**Compound expectation:** +4 to +10 pp. Plan assumes at least one lever
stalls; +4 pp (minimum viable) is the gate for `v0.3.0` to ship.

## Sprint map

| Sprint | Tag | Lever | Bench label | Gate |
|---|---|---|---|---|
| 10 | `v0.2.3` | HyDE query rewriting | `v0.2.3-hyde` | top-1 ≥ 75.9 % AND p95 ≤ 2.5 s |
| 11 | `v0.2.4` | bge-reranker-v2-m3 INT8 | `v0.2.4-bge-v2m3-int8` | top-1 ≥ previous + 1 pp AND p95 ≤ 3 s |
| 12 | `v0.3.0` | Parent-child chunking | `v0.3.0-parent-child` | top-1 ≥ 78 % AND top-10 ≥ 98 % (no recall regression) |

Strict sequential order: each sprint can only start after the previous
ships a benched tag. This preserves attribution — if Sprint 11 shows no
uplift, we roll back Sprint 11 but keep the Sprint 10 gains.

Fallback if a sprint stalls: tag the intermediate (e.g. `v0.2.3`), skip
the dead lever, proceed to next. Final tag is `v0.3.0` regardless of
which levers landed, as long as the cumulative top-1 ≥ 78 %.

---

# Sprint 10 — HyDE query rewriting (v0.2.3)

**Goal:** rewrite the user query into a Qwen2.5-1.5B-generated hypothetical
answer paragraph, embed THAT for dense retrieval, keep the original query
for BM25 and for reranker scoring. Typical HyDE pattern.

## Why Qwen2.5-1.5B-Instruct and not Phi-3.5-mini / Llama-3.2-3B

- **Qwen2.5-1.5B-Instruct Q4_K_M** is ~1 GB on disk, runs ≥ 30 tok/s on a
  12-core CPU via llama.cpp (user's host: Windows 11, Ryzen-class).
  Hypothetical answer = 80–150 tokens → 3–5 s first-token latency,
  ~150 ms/token generation. Total: **~400–800 ms per query rewrite.**
- Phi-3.5-mini is 3.8 B (Q4 ~2.5 GB), ~2× slower for 1.5–3 pp quality
  delta on general knowledge — not worth the latency for tech-docs.
- Llama-3.2-3B is worse on structured tech output in our anecdotal tests.
- Caching by exact query string collapses repeat calls to < 1 ms (dict lookup).

## Task 10.1 — Bring up llama.cpp server sidecar

**New file:** `docker/llm-compose.yaml` (extends `compose.yaml`).

**Runtime:** `ghcr.io/ggerganov/llama.cpp:server`. Reasoning:
- Already in the podman ecosystem.
- stdio MCP host can talk to it over HTTP 127.0.0.1:8090 (no network config).
- Model cached under `models/llm/Qwen2.5-1.5B-Instruct-Q4_K_M.gguf`.

**fetch-models.ps1 addition:** pull the Q4_K_M GGUF from
`huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF` (~1 GB). MinBytes guard.

**Compose snippet:**
```yaml
llm:
  image: ghcr.io/ggerganov/llama.cpp:server
  command: -m /models/Qwen2.5-1.5B-Instruct-Q4_K_M.gguf --port 8090 -c 2048 -t 8 --log-disable
  volumes: [./models/llm:/models:ro]
  ports: ["127.0.0.1:8090:8090"]
  restart: unless-stopped
```

**Gate:** `curl -s http://127.0.0.1:8090/health` returns `{"status":"ok"}`.
Smoke-test chat: POST `/v1/chat/completions` with a trivial prompt,
verify 200 + non-empty assistant message ≤ 2 s.

## Task 10.2 — `HydeExpander` in AristaMcp.Core.Retrieval

**New files:**
- `src/AristaMcp.Core/Retrieval/IHydeExpander.cs`
- `src/AristaMcp.Core/Retrieval/HydeExpander.cs` (HTTP client against llama.cpp OpenAI-compatible `/v1/chat/completions`)
- `src/AristaMcp.Core/Retrieval/NoopHydeExpander.cs` (returns the query unchanged — DI fallback when `ARISTA_MCP__Hyde__Enabled=false`)

**Contract:**
```csharp
public interface IHydeExpander
{
    // Returns a "hypothetical answer" paragraph (~80-150 tokens) suitable
    // for use as a dense-retrieval query. Short-circuits to the original
    // query when the LLM is unavailable or Hyde is disabled.
    Task<string> ExpandAsync(string query, CancellationToken ct);
}
```

**Prompt template (crucial):**
```
You are an expert in Arista EOS networking. Write one short paragraph
(2-4 sentences, ~100 words) that would answer the following question as
if it were a passage from an Arista technical document. Do not preface.
Do not hedge. Output only the paragraph.

Question: {query}
```

**Caching:** `ConcurrentDictionary<string, string>` bounded at 512 entries,
LRU-ish eviction (clear-oldest-half, same pattern as `QueryEmbeddingCache`).
Hit rate ≥ 40 % expected for bench + user repetition.

**Failure modes handled:**
- LLM HTTP timeout (> 3 s) → log warning, fall back to raw query.
- LLM response `< 20 chars` or identical to query → fall back.
- LLM 5xx → fall back + exponential-backoff breaker (10 failures in 30 s → disable for 5 min).

**Settings added to `AristaMcpSettings`:**
```csharp
public HydeSettings Hyde { get; set; } = new();

public sealed class HydeSettings
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://127.0.0.1:8090/v1/chat/completions";
    public string Model { get; set; } = "qwen2.5-1.5b-instruct";
    public int TimeoutMs { get; set; } = 3000;
    public int MaxTokens { get; set; } = 160;
    public int CacheCapacity { get; set; } = 512;
}
```

## Task 10.3 — Wire HyDE into HybridRetriever

**File:** `src/AristaMcp.Server/Retrieval/HybridRetriever.cs`

**Change point:** `HybridRetriever.SearchAsync` line 39 (intake) → 65 (embed).

**Flow:**
1. `var expanded = _queryExpander.Expand(query)` — unchanged.
2. **NEW:** `var denseSource = await _hyde.ExpandAsync(expanded.Expanded, ct)`.
3. `var denseEmbedding = await _embedder.EmbedAsync([denseSource], isQuery: true, ct)`.
4. BM25 path keeps using `expanded.Expanded` (BM25 is lexical — HyDE hallucinations hurt it).
5. Reranker keeps using `expanded.Expanded` as the query side of the pair
   (cross-encoder already has query+doc interaction; feeding it a
   hallucinated query paragraph would wash out the signal).

**DI wiring:** `ServerHosting.AddAristaMcpServices` registers `IHydeExpander`
as a singleton. `Enabled=false` (default) → `NoopHydeExpander`.

**Diagnostics:** `SearchDiagnostics` gets a new `HydeLatencyMs` +
`HydeHit` (cache bool). Added to the bench history JSONL row for
offline analysis.

## Task 10.4 — Bench and gate

**Run:**
```
ARISTA_MCP__Hyde__Enabled=true \
  dotnet run --project src/AristaMcp.Cli -- bench \
  --limit 10 --history tests/fixtures/bench-history.jsonl \
  --label v0.2.3-hyde
```

**Gate:**
- top-1 ≥ **75.9 %** (baseline 73.87 % + 2 pp, half of one σ — smallest
  signal we can trust at n=111).
- p95 latency ≤ **2500 ms** (baseline ~2400 ms + 100 ms headroom).
- top-10 ≥ 99.0 % (no recall regression from HyDE drifting off-domain).

**If gate fails:**
- Regressions on specific query buckets (e.g. hardware SKUs where the LLM
  invents a non-existent spec) → add an **acronym guard**: if query
  contains only SKU-like tokens (`[A-Z0-9-]+`), skip HyDE, use raw query.
- Retry gate. If still fails → revert HyDE, tag `v0.2.3-skip`, proceed to Sprint 11.

## Sprint 10 Definition of Done

- [ ] `docker/llm-compose.yaml` + `fetch-models.ps1` bring up Qwen2.5-1.5B.
- [ ] `IHydeExpander` + `HydeExpander` + `NoopHydeExpander` + tests (mocked HTTP).
- [ ] HyDE wired into `HybridRetriever`; BM25 + reranker paths unchanged.
- [ ] `bench-history.jsonl` has the `v0.2.3-hyde` row.
- [ ] `dotnet test` green.
- [ ] `git tag v0.2.3` + CHANGELOG entry.

---

# Sprint 11 — bge-reranker-v2-m3 INT8 (v0.2.4)

**Goal:** swap the stock MiniLM-L6 reranker for a 568 M-param XLM-R based
bge-reranker-v2-m3 quantized to INT8 so CPU p50 stays ≤ 2 s.

**Precondition:** v0.2.2 already shipped the XLM-R .NET code path
(`XlmRobertaRerankerTokenizer`, `XlmRobertaOnnxReranker`,
`RerankerFamilyDetector`). This sprint is pure asset work + one bench.

## Task 11.1 — ONNX export + INT8 quantization

**New script:** `arista-reranker-tune/scripts/export_bge_v2m3_int8.py`

**Steps:**
```python
# 1. fp32 export via optimum
from optimum.onnxruntime import ORTModelForSequenceClassification
model = ORTModelForSequenceClassification.from_pretrained(
    "BAAI/bge-reranker-v2-m3",
    export=True,
    provider="CPUExecutionProvider",
)
model.save_pretrained("checkpoints/bge-v2m3-fp32")

# 2. INT8 dynamic quantization via ORTQuantizer
from optimum.onnxruntime import ORTQuantizer
from optimum.onnxruntime.configuration import AutoQuantizationConfig
quantizer = ORTQuantizer.from_pretrained("checkpoints/bge-v2m3-fp32")
qconfig = AutoQuantizationConfig.avx512_vnni(is_static=False, per_channel=False)
quantizer.quantize(save_dir="checkpoints/bge-v2m3-int8", quantization_config=qconfig)
```

**Output sizes (expected):**
- fp32: ~2.3 GB
- INT8: ~560–600 MB

**Gate:** pytest-level sanity — load the INT8 model with
`Microsoft.ML.OnnxRuntime`, feed one (query, doc) pair, verify output
shape `[1, 1]` and score differs by < 0.1 from the fp32 reference.

## Task 11.2 — Drop assets into arista-mcp

**Paths (all auto-detected by existing `RerankerFamilyDetector`):**
- `models/reranker/model.onnx` — from `bge-v2m3-int8/model_quantized.onnx`.
- `models/reranker/sentencepiece.bpe.model` — from HF cache.
- `models/reranker/tokenizer_config.json` + `special_tokens_map.json`.

**Archive MiniLM:** `mv models/reranker → models/reranker-minilm-v0.1.4-baseline`
(preserves the baseline per user no-delete rule) then move the new bge
assets into place.

**`fetch-models.ps1` update:** URLs point at the `arista-reranker-tune`
release artefact location (plan to mirror to a private HF space or
GitHub release — decide at build time).

## Task 11.3 — CPU latency validation

**Standalone smoke test** before touching bench:
```csharp
// tests/AristaMcp.Embedding.Tests/Bgev2m3Int8LatencyTests.cs
[SkippableFact]
public async Task Int8LatencyBudget()
{
    // 30 pairs × 300-token docs, batch=8 → 4 batches.
    // Assert p95 per-batch ≤ 450 ms on CPU.
}
```

If latency > budget: fall back to **fp16 ONNX** (larger ~1.1 GB but
sometimes faster than INT8 due to better vectorisation path). If fp16
also blows the budget: reduce `RerankTopN` from 30 → 15 (already
adaptive under `AdaptiveRerank` when RRF span ≤ 0.02).

## Task 11.4 — Bench and gate

**Run:**
```
ARISTA_MCP__Hyde__Enabled=true \
  dotnet run --project src/AristaMcp.Cli -- bench \
  --limit 10 --history tests/fixtures/bench-history.jsonl \
  --label v0.2.4-bge-v2m3-int8
```

**Gate:**
- top-1 ≥ **Sprint 10 top-1 + 1 pp** (smaller uplift expected — reranker
  only moves #2 → #1 on candidates the retriever already surfaced).
- p95 latency ≤ **3000 ms**.
- top-10 identical (reranker cannot change top-K recall, so any drop
  indicates a bug).

**If gate fails:** keep MiniLM, archive v2m3 attempt at
`models/reranker-bge-v2m3-int8-attempt/`, tag `v0.2.4-skip`, proceed.

## Sprint 11 Definition of Done

- [ ] `arista-reranker-tune/scripts/export_bge_v2m3_int8.py` produces INT8 ONNX in one command.
- [ ] `models/reranker/` holds the new bge v2m3 INT8 set; MiniLM archived.
- [ ] `fetch-models.ps1` pulls the new artefacts.
- [ ] `Bgev2m3Int8LatencyTests` asserts p95/batch ≤ 450 ms.
- [ ] `bench-history.jsonl` has the `v0.2.4-bge-v2m3-int8` row.
- [ ] `git tag v0.2.4` + CHANGELOG.

---

# Sprint 12 — Parent-child chunking (v0.3.0)

**Goal:** keep dense retrieval on fine-grained 512-token "leaf" chunks
(where embeddings stay precise), but feed the reranker a larger
"parent" chunk (the full markdown section, up to ~2 k tokens) so it
has richer context for tie-breaking. Canonical small-to-big pattern.

**Why last:** biggest surgery (schema + reingest), biggest unknown
(reranker may actually score parents worse because they dilute the
signal), and the gate is the v0.3.0 tag — we want the Sprint 10/11
gains locked in before touching it.

## Task 12.1 — Schema migration

**File:** `src/AristaMcp.Data/Migrations/{timestamp}_AddParentChunkId.cs`

**Schema change:**
```sql
ALTER TABLE chunks
  ADD COLUMN parent_chunk_id BIGINT REFERENCES chunks(id) ON DELETE CASCADE,
  ADD COLUMN chunk_kind TEXT NOT NULL DEFAULT 'leaf'
    CHECK (chunk_kind IN ('leaf', 'parent'));

CREATE INDEX idx_chunks_parent_chunk_id ON chunks(parent_chunk_id)
  WHERE parent_chunk_id IS NOT NULL;

CREATE INDEX idx_chunks_kind ON chunks(chunk_kind);
```

**Constraints enforced:**
- `parent_chunk_id` nullable (a parent chunk points at NULL).
- `chunk_kind='leaf'` → `parent_chunk_id NOT NULL` (check via app layer +
  deferred FK).
- `chunk_kind='parent'` → `embedding NULL` (parents are NEVER embedded;
  they exist only for reranker hydration). This saves ~15 % of table
  size and ~5 min of reingest wall-clock.

**Rollback:** drop the 2 columns + 2 indexes. No data loss (leaf chunks
are still the primary retrieval unit).

## Task 12.2 — Extend chunker

**File:** `src/AristaMcp.Core/Chunking/SectionAwareChunker.cs`

**Current behaviour:** emits `ChunkDraft[]` where each draft is one
512-ish-token leaf slice of a section.

**New behaviour:** emits `ChunkSet` = `{ Parents: ChunkDraft[], Leaves: ChunkDraft[] }`:
- **Parents:** one per section, full section text up to 2048 tokens
  (truncate longer sections to the first 2048 + tail 256). `ChunkKind.Parent`.
  No embedding generated for these.
- **Leaves:** current 512-token slices with 64-token overlap. Each leaf
  carries `ParentIndex` (into the parents array) that becomes
  `parent_chunk_id` after the parent's DB id is known.

**Two-pass insert in `IngestService`:**
1. Insert parents (get DB ids back via `RETURNING id`).
2. Patch leaves with real `parent_chunk_id`, batch-embed, insert leaves.

**Token budget for parents:** `ChunkMaxTokens` is currently 1200 (leaves).
Add `ParentMaxTokens = 2048`. If a section exceeds 2048 tokens, the
parent holds the first 1792 + a "…[section continues]…" marker + the
last 256 (keep heading + closing context).

## Task 12.3 — Retrieval + rerank hydration

**File:** `src/AristaMcp.Server/Retrieval/HybridRetriever.cs`

**Current:** dense SQL + BM25 SQL hit the chunks table directly, RRF
fuses IDs, reranker scores `(query, chunk.Content)`.

**New:**
1. Dense + BM25 both filter `WHERE chunk_kind = 'leaf'`.
2. RRF fusion produces `topForRerank: List<FusedCandidate>` — these are leaves.
3. **Hydration step:** one SQL `SELECT id, content FROM chunks WHERE id IN (parent_ids_of_top_N)` where `parent_ids_of_top_N = top 30 leaves.Select(l => l.ParentChunkId).Distinct()`.
4. Reranker input switches: for each leaf candidate, build
   `RerankCandidate(leaf.ChunkId, parent.Content)` — score uses parent
   text, but the surfaced `ChunkId` stays the leaf (UI still gets a
   specific chunk, not a giant section).
5. If a leaf has no parent (backward-compat during partial reingest),
   fall back to leaf content. Transitional safety net.

**Subtle:** multiple leaves may share a parent. Dedup the parent fetch
(unique IDs) but keep the per-leaf scoring — same parent can appear in
multiple leaf-ranked positions, reranker breaks ties via the leaf's
RRF score on deadlock.

**Diagnostics:** `SearchDiagnostics.ParentHydrationLatencyMs`.

## Task 12.4 — Full-corpus reingest

**Wall-clock budget:** ~30 min on 12-core CPU (matches v0.1.4 ingest).
Parents cost ~0 embedding time (no embedding) but add ~5 % row count.

**Command:**
```
dotnet run --project src/AristaMcp.Cli -- ingest \
  --catalog /path/to/catalog.json --force
```

**Gate:** `SELECT chunk_kind, COUNT(*) FROM chunks GROUP BY chunk_kind;`
shows both 'leaf' and 'parent' rows; `SELECT COUNT(*) FROM chunks
WHERE chunk_kind='leaf' AND parent_chunk_id IS NULL;` returns 0.

## Task 12.5 — Bench and v0.3.0 gate

**Run:**
```
ARISTA_MCP__Hyde__Enabled=true \
  dotnet run --project src/AristaMcp.Cli -- bench \
  --limit 10 --history tests/fixtures/bench-history.jsonl \
  --label v0.3.0-parent-child
```

**Gate (final v0.3.0 ship criteria):**
- top-1 ≥ **78 %** (baseline + 4.13 pp, outside σ band).
- top-10 ≥ **98 %** (allow 1.1 pp recall regression since the dense
  space now indexes only leaves — a tight section that got fragmented
  into 4 leaves is now 4 retrieval targets instead of 1 section,
  which can actually improve recall but may regress in edge cases).
- p95 latency ≤ **3000 ms**.

**If gate fails but Sprint 10+11 hit the cumulative +4 pp target:**
ship `v0.3.0` WITHOUT parent-child (keep the schema change + migration
reversible, tag the stalled attempt as `v0.3.0-pc-skip`). The master
goal is top-1 ≥ 78 %, not a specific combination of levers.

**If Sprint 10+11 already hit ≥ 78 % before Sprint 12:** still run
Sprint 12 for the attribution data, but ship `v0.3.0` at whichever
measurement is best. Don't chase extra pp at the cost of complexity.

## Sprint 12 Definition of Done

- [ ] EF migration adds `parent_chunk_id` + `chunk_kind` + indexes; `dotnet ef database update` clean.
- [ ] `SectionAwareChunker` emits parents + leaves with cross-references.
- [ ] `IngestService` two-pass insert patches FKs correctly.
- [ ] `HybridRetriever` hydrates parent content for reranker input; leaves with no parent fall back cleanly.
- [ ] Full-corpus reingest completes < 45 min on the dev box.
- [ ] `bench-history.jsonl` has the `v0.3.0-parent-child` row.
- [ ] `git tag v0.3.0` + CHANGELOG summarising all three sprints.

---

# Risks + Mitigations

| Risk | Sprint | Mitigation |
|---|---|---|
| HyDE hallucinates Arista-specific details (wrong CLI commands in the hypothetical) | 10 | Prompt conditions on "Arista EOS" role; BM25 path stays on raw query; reranker stays on raw query — only dense is affected, and dense is already semantic-approximate. |
| HyDE latency dominates total response time | 10 | Cache at 512 entries; fall-back on 3 s timeout; breaker on repeated 5xx. |
| v2m3 INT8 quality drop washes out the size-driven uplift | 11 | Task 11.1 gate verifies INT8/fp32 score parity on sample pairs. If > 0.1 score delta, use fp16 instead. |
| v2m3 CPU latency unacceptable even at INT8 | 11 | Adaptive rerank cap (already in place) floors depth at 10 when RRF span is tight. Plus we can reduce `RerankTopN` from 30 → 15. |
| Parent-child schema migration locks prod | 12 | Migration is additive (new columns, nullable); no data rewrite, no lock escalation. Test on `arista_test` first. |
| Parent chunks dilute reranker signal for narrow queries | 12 | Leave fall-back path (no parent → leaf content) active for 10 % of randomly-sampled queries to A/B test on the bench. |
| One sprint's failure blocks the next | all | Explicit "tag-skip-proceed" policy. Each sprint is independent; failures are cost, not blockers. |

---

# Out of Scope (explicit deferrals)

- **LLM-as-reranker.** Qwen2.5-3B listwise reranking often beats cross-encoders
  by 5–10 pp but CPU latency is 3–8 s/query — kills interactive use. Revisit
  only if v0.3.0 gate fails entirely.
- **Embedder fine-tune.** Arctic-embed-m on (query, positive) pairs via
  MNRLoss. Skipped because our top-10 is already 99.1 % — tuning the
  embedder is a recall play, not a top-1 play.
- **Multi-query retrieval.** Generate N query paraphrases, union results.
  Smaller expected uplift than HyDE; redundant once HyDE ships.
- **Cohere rerank v3 API.** Best-in-class but requires network + $ + data
  egress — user has a CPU-only, no-network constraint.

---

# Sources

- HyDE: [Haystack Hypothetical Document Embeddings](https://docs.haystack.deepset.ai/docs/hypothetical-document-embeddings-hyde), [Zilliz HyDE writeup](https://zilliz.com/learn/improve-rag-and-information-retrieval-with-hyde-hypothetical-document-embeddings).
- bge-reranker-v2-m3: [HF model card](https://huggingface.co/BAAI/bge-reranker-v2-m3), [agentset.ai details](https://agentset.ai/rerankers/baaibge-reranker-v2-m3).
- ONNX INT8 quantization: [optimum docs quicktour](https://huggingface.co/docs/optimum/main/en/quicktour), [promptlayer bge-reranker-v2-m3-onnx-o3-cpu](https://www.promptlayer.com/models/bge-reranker-v2-m3-onnx-o3-cpu).
- Parent-child retrieval: [LangChain ParentDocumentRetriever](https://python.langchain.com/docs/how_to/parent_document_retriever/), [Parent-Child Chunking in LangChain for Advanced RAG](https://medium.com/@seahorse.technologies.sl/parent-child-chunking-in-langchain-for-advanced-rag-e7c37171995a).
- Qwen2.5 CPU inference: [Qwen llama.cpp docs](https://qwen.readthedocs.io/en/latest/run_locally/llama.cpp.html).
