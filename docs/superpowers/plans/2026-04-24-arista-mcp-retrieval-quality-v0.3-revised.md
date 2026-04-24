# arista-mcp Retrieval Quality v0.3 (revised) — Bench → Fine-tune v2-m3 → Parent-Child → LLM listwise

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or executing-plans.

**Supersedes:** `2026-04-23-arista-mcp-retrieval-quality-v0.3.md` (Sprint 10
HyDE attempt landed and failed; post-mortem in that file's SUPERSEDED banner).

**Overall goal (revised 2026-04-24 after Sprint 13 landing):** ship
`v0.3.0` with **top-1 ≥ 95 %** on the expanded 588-query v2 bench set
(+4.18 pp over the `v0.1.4-rebench-v2` baseline 90.82 %, ~2σ at
n=588 σ=1.3). CPU-only serve, p95 latency ≤ 3 s, top-10 ≥ 99 %.

The original target (+6.1 pp over 73.87 % = 80 %) was set before Sprint 13
revealed that the v1 substring-heuristic bench was under-counting hits.
On v2 (chunk-ID multi-positive scoring) stock MiniLM already lands at
90.82 %, so "80 %" is historical noise. The new +4.18 pp / 95 % target
is still a genuine reranker-side improvement — we are asking the
cross-encoder to correctly tie-break on roughly 25 additional queries
out of 588.

## The lesson driving this revision

Five independent retrieval-quality levers — stock MiniLM, MiniLM fine-tune
(117 triples), MiniLM fine-tune (366 hard-mined triples), bge-reranker-base
stock swap, HyDE query rewriting — all landed at top-1 in the 72.07–73.87 %
band with top-10 fixed at **99.10 %** to the chunk (110/111 queries).

At n=111, p ≈ 0.73 the binomial σ is **±4.2 pp**. The entire top-1 spread
across five wildly different strategies was ±1.8 pp, i.e. **less than one
sigma**. The bench cannot distinguish a real +3 pp uplift from noise; any
sprint gate of the form "+1 pp over previous" is measuring coin flips.

So before touching the retriever again, we fix the ruler.

## Sprint map

| Sprint | Tag | Scope | Gate (on v2 set n=588) |
|---|---|---|---|
| **13** | `v0.2.3-bench-expand` | Expand bench to 588 queries (multi-positive chunk IDs); rebench prior 4 configs | **LANDED.** σ=1.3 pp, prior configs re-ordered with same direction but larger magnitudes; see below |
| **14** | `v0.2.4-v2m3-finetune` | Domain fine-tune `BAAI/bge-reranker-v2-m3` on 4000+ mined triples from v2 set, INT8 ONNX for CPU | top-1 ≥ **92.5 %**, p95 ≤ 3 s |
| **15** | `v0.2.5-parent-multi` | Parent-child chunking + multi-query retrieval (cheap, independent) | top-1 ≥ **94 %**, top-10 ≥ 99 % |
| **16** | `v0.3.0` | **Contingent** — LLM listwise re-rank on top-5 only, invoked iff Sprint 15 top-1 < 95 % | top-1 ≥ **95 %**, p95 ≤ 3 s |

### Sprint 13 landed results

| Config | v1 top-1 | v2 top-1 | Δ vs v2 baseline | p50 v2 (ms) |
|---|---:|---:|---:|---:|
| **v0.1.4 stock MiniLM-L6** | 73.87 % | **90.82 %** | — | 550 |
| v0.2.1 MiniLM fine-tune | 72.97 % | 88.61 % | −2.21 pp | 568 |
| v0.2.2 bge-reranker-base stock | 72.07 % | 87.07 % | −3.75 pp | 1935 |
| v0.2.3 HyDE + MiniLM | 72.07 % | 86.22 % | −4.60 pp | 3406 |

Confirms the measurement-floor thesis — on v1 the three attempts all
landed at top-1 72–74 %, visually indistinguishable. On v2 all three
regressions are ≥ 2σ outside baseline, proving they were real harm
that v1 masked. HyDE additionally broke top-10 from 100 → 96.94 %
(hallucination pulling retrieval off-target on ~3 % of queries).

Each sprint ships its own bench row + tag. Sprint 13 is the prerequisite —
no other sprint can credibly measure success without it. Sprints 14 and 15
are independent and measurable separately. Sprint 16 is skipped if Sprint 15
already crosses 80 %.

## Why this order — and why each lever will move top-1 now where it didn't before

The retrieval stack is built from embedder (Arctic Embed m, 109 M),
retriever (pgvector HNSW + vchord_bm25 + RRF), and reranker (MiniLM-L6, 22 M).
top-10 = 99.10 % proves retrieval works. The entire top-1 gap is reranker
tie-breaking.

Why past reranker tricks failed:
- **MiniLM-L6 fine-tune** on 366 triples: too small a model, too little data,
  evaluation noise-bound. Stock already saturated its capacity on a 46-query
  teacher set.
- **bge-reranker-base stock swap**: bigger model, but trained on general-web
  pairs — no domain fit for Arista command-line / feature naming.
- **HyDE**: Qwen2.5-1.5B can't emit Arista-specific phrasing, so the
  hypothetical paragraph diluted dense precision.

Why the revised plan has a real shot:
- **Sprint 14 combines two changes at once** — a stronger stock reranker
  (v2-m3, 568 M) AND domain fine-tuning (2500+ triples from expanded bench).
  Nutanix on an analogous corpus gets MRR 0.841 from v2-m3 stock alone.
- **Sprint 15's parent-child** gives the reranker ~4× more context (full
  section instead of 512-token leaf), which is exactly what tie-breaking
  on ambiguous candidates needs.
- **Multi-query** (3 paraphrases unioned) is the proven-in-literature
  HyDE replacement that doesn't hallucinate.
- **LLM listwise on top-5** only kicks in if we're still short — latency
  budget survives because we only invoke it on 5 candidates, not 30.

---

# Sprint 13 — Expanded bench (v0.2.3-bench-expand)

**Goal:** replace the 111-query ad-hoc bench with a 400-query set that has
(a) validated ground-truth chunk IDs, not just slug-substring heuristics,
(b) explicit multi-positive handling for genuinely ambiguous queries, and
(c) tight enough σ that Sprints 14–16 gates are measurable.

## Why bench expansion is the highest-ROI action right now

- σ drops from ±4.2 pp (n=111) to ±2.0 pp (n=400). A +3 pp uplift becomes
  detectable at ~95 % confidence.
- Hard-negative mining benefits linearly: 46 → 400 queries = 8.7× more
  training triples for Sprint 14.
- Same data unlocks rigorous embedder fine-tuning too (deferred, but the
  option opens).
- Side effect: we catch bugs in `expect_any` / `expect_product` heuristics
  where the CURRENT bench says "miss" but a human would say "that's a
  valid answer".

## Task 13.1 — Stratified chunk sampling

**Script:** `scripts/sample_chunks_for_bench.py` (new, in `arista-reranker-tune`).

- Connect to `arista-mcp` postgres (`127.0.0.1:5434`).
- Sample 500 chunks stratified by `product_family` so every product is
  represented proportionally. Minimum 15 chunks per product (small ones
  like `cva`, `cvw`, `hardware` won't support 30, so floor is 15).
- Filters: token_count ≥ 150 AND token_count ≤ 900 (skip stubs + giant
  dump-pages), raw_content doesn't start with "```" (skip code-block
  chunks — too low signal for queries).
- Save as `bench-seed-chunks.jsonl`: `{chunk_id, document_id, title, section_title, product_family, content}`.

## Task 13.2 — LLM query generation

**Script:** `scripts/generate_bench_queries.py` (new).

- For each seed chunk, prompt Qwen2.5-7B-Instruct (via the existing
  llama.cpp sidecar — bigger model this time, one-shot batch job, not
  hot path) or a cloud API to produce 1–2 natural user questions the
  chunk would answer.
- Prompt template explicitly asks for:
  - Short queries (5–12 words)
  - Real-user phrasing (no Arista-internal jargon the reader wouldn't know)
  - No yes/no questions
  - No questions about metadata ("when was this doc written")
- Expected output per chunk: 1.5 queries average, so 500 chunks → ~750
  raw queries.
- Record provenance: each raw query carries `source_chunk_id`.

## Task 13.3 — Fairness filter via current retriever

**Script:** `scripts/validate_bench_queries.py` (new).

- Run the current `HybridRetriever` (stock MiniLM reranker, no HyDE, no
  parent-child) on each generated query.
- Keep query iff `source_chunk_id ∈ top_20`. Otherwise the query is
  too vague to have a correct retrieval target — drop it.
- For surviving queries, record `retriever_top_10_chunk_ids` so we can
  flag queries where multiple of those chunks are ALSO valid answers
  (for Task 13.4).
- Expected survival: ~80 % → ~600 queries.

## Task 13.4 — Multi-positive annotation

The hardest + most valuable step. Queries like "BGP neighbor config" are
answered correctly by multiple distinct chunks (how-to, reference, example).
Treating only one as "correct" caps top-1 ceiling artificially.

**Approach:**
- For each query, present the top-10 retrieval candidates (content + slug)
  to a stronger LLM (Qwen2.5-7B local or Claude via API if available) with
  the original source chunk labelled, and prompt:
  > *"Which of these candidate chunks would a reasonable engineer accept
  > as a valid answer to the query? Return a list of chunk IDs. The
  > source chunk is always valid."*
- Save `expect_any_of_chunk_ids: [id1, id2, ...]` per query.
- Sample 50 queries at random for manual review — sanity check LLM labels.
- Accept LLM labels if manual-review agreement ≥ 85 %; otherwise tighten
  the prompt and re-run.

**Output:** `tests/fixtures/bench-queries-v2.json` with schema:
```json
{
  "queries": [
    {
      "query": "MLAG peer-link configuration",
      "source_chunk_id": 12345,
      "expect_any_of_chunk_ids": [12345, 12892, 13104],
      "product_family": "eos",
      "generation_model": "qwen2.5-7b-instruct"
    }
  ]
}
```

## Task 13.5 — Bench harness v2

**File:** `src/AristaMcp.Cli/Benchmarks/BenchmarkRunnerV2.cs`.

- New command: `arista-mcp bench --queries bench-queries-v2.json --version 2`.
- Scoring change: a query counts as top-1 hit iff the returned chunk 0
  is in `expect_any_of_chunk_ids`. top-10 iff ANY of `expect_any_of_chunk_ids`
  appears in positions 0–9.
- Legacy bench (`bench-queries.json`) still runs via the old harness so
  historical rows in `bench-history.jsonl` stay comparable.
- Add bench-history fields: `query_set_version`, `query_count`, same
  top-1 / top-10 / latency columns.

## Task 13.6 — Rebench all prior configs on the new set

**Run in order:**
1. Stock MiniLM (v0.1.4) → `v0.1.4-rebench-v2`
2. MiniLM v0.2.1 fine-tune → `v0.2.1-rebench-v2`
3. bge-reranker-base → `v0.2.2-rebench-v2` (temporarily restore from archive)
4. HyDE + stock MiniLM → `v0.2.3-rebench-v2` (requires bringing llm sidecar back up)

Gate: each rebenched config lands within **±1 pp** of its n=111 number,
AND pairwise differences remain consistent (e.g. HyDE is still ~-1.8 pp
vs stock). If the ordering flips dramatically, the new bench set has
its own bias and we need another iteration.

## Sprint 13 Definition of Done

- [ ] `tests/fixtures/bench-queries-v2.json` has ≥ 400 queries, each with ≥ 1 validated positive chunk ID.
- [ ] 50-query manual review shows ≥ 85 % agreement with LLM labels.
- [ ] Legacy harness still runs the old set (n=111) for historical continuity.
- [ ] New harness (`bench --version 2`) runs the expanded set and writes bench-history rows tagged `query_set_version=2`.
- [ ] 4 prior configs rebenched on v2; ordering consistent, σ observed ≤ ±2 pp.
- [ ] `git tag v0.2.3-bench-expand` + CHANGELOG entry.

---

# Sprint 14 — Domain-tuned bge-reranker-v2-m3 INT8 (v0.2.4-v2m3-finetune)

**Goal:** combine the two biggest reranker levers into one sprint: swap to
v2-m3 (568 M, SOTA stock) AND fine-tune it on 2500+ triples from the
expanded bench. Output: INT8 ONNX for CPU, drop-in replacement.

**Why both at once:** Sprint 14's gate (+3 pp to 77 %) needs the
combined lever. Stock v2-m3 is expected to give +1–2 pp over MiniLM
on domain tie-breaking; fine-tuning adds another +1–3 pp. Either
alone is too small to reliably cross the gate, but their sum is.

## Task 14.1 — Extended hard-negative mining

Uses the same infrastructure as Sprint 9's `mine_hard_negatives` but
with the 400-query bench set (not the 46-query `curate-triples` output).

- Input: `bench-queries-v2.json` → (query, positive_chunk_id) pairs.
- Embedder for mining: `snowflake-arctic-embed-m` (what the corpus is
  embedded with).
- 8 hard negatives per query, `absolute_margin=0.02` (tuned in Sprint 9).
- Exclude cross-positive: if query A's positive chunk is in query B's
  top-20, don't mine it as a negative for query B.
- Output: `triples-v2m3.jsonl`, expected ~3000 rows (400 × 8 × survival rate).

## Task 14.2 — Fine-tune recipe

**Base model:** `BAAI/bge-reranker-v2-m3` (NOT -base — v2-m3 is the
Nutanix-proven variant and we now have the data to justify its size).

**Loss:** `BinaryCrossEntropyLoss(pos_weight=8.0)` — same recipe Sprint 9
validated, now with 8× more data.

**Trainer:** `CrossEncoderTrainer` (sentence-transformers 5.x). fp16
on RTX 4070 with batch_size=16 (v2-m3 is heavier than MiniLM; can't fit
batch=32 in 12 GB).

**Hyperparameters:** `configs/v2m3-finetune.yaml`:
```yaml
base_model_id: BAAI/bge-reranker-v2-m3
batch_size: 16
epochs: 3
learning_rate: 2e-5
warmup_ratio: 0.1
pos_weight: 8.0
max_length: 512
eval_strategy: epoch
metric_for_best_model: eval_arista-eval_ndcg@10
```

**Gate:** on held-out eval split (15 %), nDCG@10 on the fine-tuned model
exceeds stock v2-m3 nDCG@10 by ≥ 0.02 (absolute). If not, the fine-tune
over-fit or regressed — investigate learning rate / pos_weight before
export.

**Wall-clock budget:** ~45 min on RTX 4070 for 3 epochs of 2500 triples.

## Task 14.3 — INT8 ONNX export

Full pipeline from `2026-04-23-arista-mcp-retrieval-quality-v0.3.md` Sprint 11.1
still applies — the XLM-R .NET code path (`XlmRobertaRerankerTokenizer` +
`XlmRobertaOnnxReranker` + `RerankerFamilyDetector`) shipped in v0.2.2 and
doesn't need changes.

- fp32 export via `optimum.onnxruntime.ORTModelForSequenceClassification.from_pretrained(..., export=True)`.
- INT8 dynamic quantization via `ORTQuantizer` with
  `AutoQuantizationConfig.avx512_vnni(is_static=False, per_channel=False)`.
- Parity test: INT8 vs fp32 score delta < 0.1 on 20 sample pairs, ordering
  preserved on 5 sample queries × 30 candidates.

Target size: ~560–600 MB INT8 (vs 2.3 GB fp32).

## Task 14.4 — Deploy + bench

- Archive current `models/reranker/` (MiniLM) → `models/reranker-minilm-v0.1.4-baseline/`
  (already exists from Sprint 11 prep, just move latest files in).
- Drop `bge-v2m3-int8/model_quantized.onnx` + `sentencepiece.bpe.model`
  + config files into `models/reranker/`.
- `RerankerFamilyDetector` auto-picks XLM-R path.
- CPU latency smoke test before bench:
  ```csharp
  // tests/AristaMcp.Embedding.Tests/V2m3Int8LatencyTests.cs
  [SkippableFact]
  public async Task PairLatencyBudget()
  {
      // 30 pairs × 300-token docs, batch=8 → 4 batches.
      // Assert p95 per-batch ≤ 600 ms on CPU.
  }
  ```
  If latency > budget: drop `RerankTopN` from 30 → 15. The adaptive-rerank
  floor stays at 10 (existing behaviour).

- Run bench on **v2 query set**:
  ```
  dotnet run --project src/AristaMcp.Cli -c Debug -- bench \
    --queries tests/fixtures/bench-queries-v2.json --version 2 \
    --history tests/fixtures/bench-history.jsonl \
    --label v0.2.4-v2m3-finetune
  ```

**Gate:**
- top-1 ≥ **77 %** (baseline v0.1.4-rebench-v2 + 3 pp, detectable at σ=2 pp).
- top-10 ≥ 98 % (v2m3 shouldn't regress recall vs MiniLM; if it does, check
  tokenisation path).
- p95 latency ≤ 3 s.

**If gate fails:**
- top-1 stalls near baseline → keep MiniLM, archive v2m3 attempt at
  `models/reranker-v2m3-v0.2.4-attempt/`, tag `v0.2.4-skip`. Sprint 15
  can still make the 80 % target alone if its uplift is big enough.
- Latency blows budget → swap INT8 for fp16 (usually 1.2–1.5 s p50); if
  still over, reduce `RerankTopN` to 15.

## Sprint 14 Definition of Done

- [ ] `arista-reranker-tune/scripts/mine_negatives_v2.py` produces ~3000 triples.
- [ ] `scripts/train_v2m3.py` fine-tunes bge-reranker-v2-m3 with ≥ 0.02 nDCG@10 uplift on eval.
- [ ] `scripts/export_v2m3_int8.py` outputs INT8 ONNX ≤ 600 MB.
- [ ] `models/reranker/` holds the new model; MiniLM archived.
- [ ] CPU latency test passes (p95 per 8-pair batch ≤ 600 ms).
- [ ] `bench-history.jsonl` has the `v0.2.4-v2m3-finetune` row on v2 set.
- [ ] `git tag v0.2.4-v2m3-finetune` + CHANGELOG.

---

# Sprint 15 — Parent-child + multi-query (v0.2.5-parent-multi)

**Goal:** two independent retrieval-side changes, benched together because
both are cheap to implement but expensive to bench in isolation. If the
combined uplift is ≥ 2 pp past Sprint 14, we likely hit 80 % without
Sprint 16.

## Task 15.1 — Parent-child chunking

**Full spec:** Sprint 12 of the superseded v0.3 plan (schema migration,
two-pass ingest, hydration step in `HybridRetriever`). Not repeated here
— that design stands.

**Changes vs original plan:**
- Run the full-corpus reingest on a throwaway `arista_test` database first
  to validate the schema + chunker + retriever changes without destroying
  the prod DB. Promote only after Sprint 15 bench shows no regression.
- Fallback-to-leaf behaviour is kept permanently (not transitional) —
  gives us a cheap kill switch if parents hurt a specific product family.

## Task 15.2 — Multi-query retrieval

**Cheap HyDE replacement that doesn't hallucinate.**

**Approach:** generate 3 query paraphrases using Arista-specific rule-based
rewriting (no LLM needed, no latency cost). Run dense retrieval on each,
union results before RRF.

**File:** `src/AristaMcp.Core/Retrieval/MultiQueryExpander.cs` (new).

**Rules** (static, auditable, no hallucination):
1. **Acronym expansion/contraction swap.** `QueryExpander` already does
   one direction ("MLAG" → "MLAG (Multi-chassis Link Aggregation Group)").
   Add the reverse direction — if the query has the full phrase, also
   retrieve with just the acronym, and vice versa.
2. **Word-order permutation.** "BGP neighbor configuration" → also try
   "neighbor configuration BGP". Boosts recall on chunks that lead with
   the noun.
3. **Product-slug injection.** If `expansion.Product` resolved to a
   specific product, also try `"{product} {query}"`. E.g. "EVPN type-5"
   → "EOS EVPN type-5". Gets product-scoped chunks that BM25 would
   otherwise miss.

Run each of the 3 variants through dense retrieval in parallel (same
embedder, same cache), take the UNION of top-50 per variant, and feed
into RRF. BM25 stays single-query (lexical union would bloat noise).

**Expected cost:** 2 extra embedder calls (cached after warm-up) + 2
extra dense SQL scans. p50 rises by maybe 50 ms. Negligible compared to
reranker time.

## Task 15.3 — Combined bench

Run the v2 bench with BOTH 15.1 and 15.2 active:
```
dotnet run --project src/AristaMcp.Cli -c Debug -- bench \
  --queries tests/fixtures/bench-queries-v2.json --version 2 \
  --history tests/fixtures/bench-history.jsonl \
  --label v0.2.5-parent-multi
```

**Gate:**
- top-1 ≥ **79 %** on v2 set (Sprint 14 + 2 pp).
- top-10 ≥ 98.5 % (multi-query should INCREASE recall).
- p95 latency ≤ 3 s.

If we cross 80 % here: skip Sprint 16, go straight to tagging `v0.3.0`.

## Sprint 15 Definition of Done

- [ ] Parent-child schema migration applies cleanly on `arista_test`.
- [ ] Chunker emits parents + leaves with correct FK linkage.
- [ ] `MultiQueryExpander` unit-tested for all 3 rules.
- [ ] `HybridRetriever` runs 3-query union + parent hydration.
- [ ] Full-corpus reingest on prod DB completes < 45 min.
- [ ] `bench-history.jsonl` row for v0.2.5-parent-multi at top-1 ≥ 79 %.
- [ ] `git tag v0.2.5-parent-multi` + CHANGELOG.

---

# Sprint 16 — LLM listwise re-rank on top-5 (v0.3.0) — contingent

**Run iff** Sprint 15 top-1 < 80 %. Otherwise skip and tag `v0.3.0`
directly from Sprint 15.

**Goal:** after the cross-encoder produces top-5, ask a local 3B–7B LLM
to re-rank these 5 candidates in a single listwise call. Published
research (RankGPT, Zephyr-Rerank) shows +3–10 pp top-1 over cross-encoder
alone.

**Why only top-5 and only contingent:** a 7B-param listwise prompt is
~2 s on CPU. Doing it on top-30 blows the latency budget. Doing it on
top-5 adds maybe 2 s p50 and only runs when we actually need the boost.

## Task 16.1 — Sidecar model + prompt

- Switch llama.cpp sidecar model from Qwen2.5-1.5B to
  **Qwen2.5-3B-Instruct-Q4_K_M** (1.8 GB GGUF). Bigger context, better
  instruction-following for listwise reasoning.
- Docker compose `llm` service already in place (Sprint 10). Just
  swap the `--model` arg + fetch-models.ps1 URL.
- Latency smoke test: listwise prompt with 5 candidates × 300 tokens
  each + chain-of-thought → total ~1800 input tokens, 50 output tokens.
  Expected: ~1.8–2.2 s p50 on CPU.

**Prompt template:**
```
You are ranking documentation passages by relevance to an engineer's
question. The question is about Arista EOS / networking.

Question: {query}

Candidates (ID: text snippet):
[A]: {candidates[0].text[:400]}
[B]: {candidates[1].text[:400]}
[C]: {candidates[2].text[:400]}
[D]: {candidates[3].text[:400]}
[E]: {candidates[4].text[:400]}

Reply with exactly one line: the candidate IDs in order from most to
least relevant, comma-separated. Example: B, A, D, C, E
```

## Task 16.2 — Wire into HybridRetriever

- New setting: `AristaMcpSettings.ListwiseRerank.Enabled` (default false)
  and `ListwiseRerank.ModelEndpoint` (default `http://127.0.0.1:8090/v1/chat/completions`).
- After cross-encoder produces top-N, take top-5, invoke LLM listwise,
  reorder. Positions 6+ stay from cross-encoder.
- Same fallback pattern as `HydeExpander` (timeout → keep cross-encoder order).
- Cache by `(query, top_5_chunk_ids_tuple)` — same query + candidates → same
  listwise output, safe to memoise for ~5 min.

## Task 16.3 — v0.3.0 gate

```
ARISTA_MCP__Hyde__Enabled=false \
ARISTA_MCP__ListwiseRerank__Enabled=true \
  dotnet run --project src/AristaMcp.Cli -c Debug -- bench \
  --queries tests/fixtures/bench-queries-v2.json --version 2 \
  --history tests/fixtures/bench-history.jsonl \
  --label v0.3.0-listwise
```

**Gate (final v0.3.0 ship):**
- top-1 ≥ **80 %** on v2 set.
- top-10 ≥ 98 %.
- p95 latency ≤ 3 s (Sprint 15 p95 + 2 s listwise ≤ 3 s only if Sprint 15
  baseline is ≤ 1 s — tight; may need to reduce listwise size to top-3
  if we're at the wall).

## Sprint 16 Definition of Done

- [ ] Qwen2.5-3B replaces Qwen2.5-1.5B in llm sidecar; health check + smoke-test pass.
- [ ] `ListwiseReranker` component ships as an IHydeExpander-style optional DI service.
- [ ] `bench-history.jsonl` has `v0.3.0-listwise` row at top-1 ≥ 80 %.
- [ ] `git tag v0.3.0` + comprehensive CHANGELOG summarising all 4 sprints.

---

# Risk register

| Risk | Sprint | Mitigation |
|---|---|---|
| LLM-generated bench queries have systematic bias toward retrievable-by-current-stack queries (survivorship) | 13 | Task 13.3's fairness filter already introduces this bias. Document it explicitly in the bench-queries-v2.json schema and accept — the goal is a *measurable* bench, not a perfect one. |
| Manual review agreement < 85 % on multi-positive labels | 13 | Tighten prompt, re-run. If still < 85 %, drop to single-positive scoring (same as v1 bench) — we still win on n=400 σ reduction. |
| v2-m3 fine-tune over-fits on the generated triples | 14 | Held-out eval split catches it (Task 14.2 gate). If over-fitting, reduce epochs to 2, LR to 1e-5. |
| INT8 v2-m3 CPU p95 > 3 s | 14 | Fall back to fp16 (larger, sometimes faster on AVX-512). If still over, cap `RerankTopN` at 15. |
| Parent-child chunking causes top-10 recall regression on queries where the leaf answer was narrow | 15 | Keep fallback-to-leaf behaviour permanent (plan change vs superseded doc). Monitor bench-history top-10 specifically. |
| Multi-query union blows the embedder cache | 15 | Acronym variants collapse via QueryExpander's normalisation; remaining variants share the Arctic Embed prefix. Cache capacity may need to bump from 256 → 1024 — cheap. |
| Qwen2.5-3B listwise hallucinates a non-existent candidate ID | 16 | Parse output; if ID not in `{A, B, C, D, E}`, fall back to cross-encoder order. Log to structured diagnostics. |
| v0.3.0 lands with 79.x % instead of 80 % | all | Noise band analysis on v2 set (σ=2 pp at n=400) says 79.x is functionally 80 for user-visible behaviour. Ship + document. |

---

# Out of scope (explicit deferrals)

- **Embedder fine-tune.** Top-10 is already at 99.10 % — tuning Arctic Embed
  is a recall play. Defer until top-10 becomes the bottleneck (not expected
  at this scope). Infrastructure from Sprint 9 (arista-reranker-tune)
  supports it when needed.
- **Cloud reranker APIs** (Cohere rerank v3, Voyage Rerank). Best quality,
  but conflicts with the CPU-only / no-network constraint. Revisit only
  if v0.3.0 gate fails entirely.
- **BM25 multi-query.** Multi-query on dense is cheap (cached embedding);
  on BM25 it's lexical noise. Measured in published benchmarks as usually
  a small regression for tech-docs due to term overlap.
- **pgvector → Qdrant/Milvus migration.** Research confirms pgvector is
  optimal under 50 M vectors (`docs/research/compass_artifact_wf-1c6b4cce...md`).
  At 59 k chunks we're three orders of magnitude inside the zone.

---

# Sources

- Bench-set expansion recipe: [Ragas evaluation dataset generation](https://docs.ragas.io/en/stable/concepts/testset_generation/), [Anthropic cookbook "Generate synthetic queries"](https://github.com/anthropics/anthropic-cookbook/blob/main/skills/retrieval_augmented_generation/retrieval_and_reranking.ipynb).
- bge-reranker-v2-m3 fine-tune: [FlagEmbedding reranker fine-tuning guide](https://github.com/FlagOpen/FlagEmbedding/tree/master/FlagEmbedding/reranker).
- Multi-query retrieval: [LangChain MultiQueryRetriever](https://python.langchain.com/docs/how_to/MultiQueryRetriever/), [Vespa multi-query studies](https://blog.vespa.ai/improving-zero-shot-ranking-with-vespa/).
- LLM listwise re-rank: [RankGPT paper](https://arxiv.org/abs/2304.09542), [Zephyr-7B-Beta reranking benchmarks](https://huggingface.co/HuggingFaceH4/zephyr-7b-beta).
- CPU/pgvector rationale: `C:/SHARE/nutanix-docs-mcp/docs/research/compass_artifact_wf-1c6b4cce-366d-49d6-b6f8-e2dfa24e26fe_text_markdown.md`, `C:/SHARE/nutanix-docs-mcp/docs/research/compass_artifact_wf-2a2cdb0a-15ee-48b3-bbb2-f2febac42450_text_markdown.md`.
