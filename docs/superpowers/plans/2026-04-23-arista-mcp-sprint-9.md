# arista-mcp Sprint 9 Implementation Plan — Reranker Fine-Tune (GPU Train, CPU Serve)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or executing-plans.

**Goal:** Ship `v0.2.0` with a domain-tuned cross-encoder reranker. Training
happens once on GPU (RTX 4070); the result exports to ONNX and slots into
`models/reranker/` without any arista-mcp runtime change. Target uplift:
**top-10 ≥ 95 %, top-1 +5 pp** over the `v0.1.4-full-corpus-crlf` baseline.

**Prerequisites:**
- `v0.1.4` tagged (full-corpus bench row + curate-triples output live).
- `tests/fixtures/reranker-triples.jsonl` has ≥ 500 triples (from
  `arista-mcp curate-triples` — Sprint 8.5 deliverable).
- Disk: ~4 GB for HF model cache + 1 GB for ONNX exports.
- Time: ~2 h wall (fine-tune) + ~30 min (export + bench) per iteration.

---

## Sprint 9 Overview

| # | Task | Prio | Gates |
|---|------|------|-------|
| 9.0 | Triples augmentation (paraphrases + hard-mined) | 🟡 data | ≥ 1000 total triples, 80/20 train/eval split |
| 9.1 | `arista-reranker-tune` Python repo scaffolding  | 🔴 blocker | `uv sync`, `train.py`, `eval.py`, pinned deps |
| 9.2 | Cross-encoder margin loss training loop         | 🔴 core | base model ms-marco-MiniLM-L6-v2; 3-5 epochs; eval MRR@10 uplift ≥ 0.05 |
| 9.3 | ONNX export with `optimum`                      | 🔴 blocker | drop-in replacement for v0.1.0 reranker model |
| 9.4 | arista-mcp bench on tuned weights               | 🟡 metric | label `v0.2.0-finetuned-reranker` |
| 9.5 | Regression guard: fixed eval split in tests/    | 🟡 quality | catch accidental tokenizer/vocab drift |
| 9.6 | Docs + CLAUDE.md additions                      | 🟢 release | training reproducibility, weight update flow |
| 9.7 | `v0.2.0` tag                                    | 🏁 release | CHANGELOG + hand-off |

**Definition of Done:**
- [ ] `arista-mcp bench --label v0.2.0-finetuned-reranker` shows top-10 ≥ 95 %,
      top-1 ≥ baseline + 5 pp.
- [ ] `models/reranker/` holds the tuned ONNX + vocab; file SHA differs from
      the stock bge-reranker checkpoint shipped in v0.1.0.
- [ ] `arista-reranker-tune/` has reproducible training: `uv run train.py`
      with fixed seed re-produces a checkpoint within 1 pp of the shipped one.
- [ ] No arista-mcp source-code change required — `OnnxReranker` loads the new
      weights transparently.
- [ ] `v0.2.0` tag + CHANGELOG entry exist.

---

## Task 9.0: Triples augmentation

**Files (new repo, `../arista-reranker-tune`):**
- `scripts/augment_triples.py`
- `data/triples-augmented.jsonl`

**Problem:** 111 bench queries × 4 negatives = ~440 triples, minus drops for
insufficient cross-product diversity. Likely ~300-400 raw. Fine-tuning a
cross-encoder on 300 examples overfits hard.

**Solution:**

1. **Paraphrases (cheap).** For each query, generate 2-3 paraphrases via a
   small LLM call (or a rule-based rewriter: synonym swap, word order,
   acronym expansion/contraction). Keep the same (positive, negatives).
   Triples triple, overfit risk drops.

2. **Hard-negative mining from the full corpus.** For each (query, positive)
   pair from `bench-queries.json`, pull top-20 from the current
   `HybridRetriever` on the `v0.1.4-full-corpus-crlf` DB, excluding the
   positive's document and product. The first 4 become the hard negatives;
   next 4 are augmentation hard-negatives. Gives ~8 hard-negs per query.

3. **Random negatives for diversity.** 2 random chunks per query (uniform
   sample from chunks table where product ≠ positive.product). Cheap
   regularisation against the reranker overfitting to the hard-neg
   distribution.

Total after augmentation: target ≥ 1000 triples. 80/20 train/eval split
is deterministic — hash the query string, modulo 5, split == 0 → eval.

**Deliverable:** `data/triples-augmented.jsonl` with fields
`{query, positive, negatives, split: "train"|"eval"}`.

---

## Task 9.1: `arista-reranker-tune` repo scaffolding

**New repo at `../arista-reranker-tune`.**

```
arista-reranker-tune/
├── pyproject.toml       # [project] name="arista-reranker-tune", python=">=3.11"
├── README.md
├── uv.lock
├── src/
│   └── arista_reranker_tune/
│       ├── __init__.py
│       ├── triples.py       # TripleDataset (query, positive, negatives)
│       ├── model.py         # load_base, save_checkpoint
│       ├── train.py         # MultipleNegativesRankingLoss loop
│       ├── eval.py          # MRR@10 + nDCG@10 on held-out split
│       └── export.py        # optimum → ONNX
├── scripts/
│   └── augment_triples.py   # 9.0 above
├── configs/
│   └── default.yaml         # epochs, lr, batch, base_model_id
├── data/
│   └── (gitignored — populated from arista-mcp/tests/fixtures/)
└── checkpoints/
    └── (gitignored)
```

**Dep pins (`pyproject.toml`):**
- `torch = "^2.4"` (CUDA 12.4 wheels; RTX 4070 ready)
- `transformers = "^4.46"`
- `sentence-transformers = "^3.3"` (cross-encoder with margin loss out of box)
- `datasets = "^3.1"`
- `optimum[onnxruntime] = "^1.25"`
- `pydantic = "^2"` (config validation)
- `tqdm`
- Dev: `pytest`, `ruff`, `ty`

**Gate:** `uv sync` runs clean; `uv run python -c "import torch; assert torch.cuda.is_available()"` passes.

---

## Task 9.2: Training loop

**File:** `src/arista_reranker_tune/train.py`

**Base model:** `cross-encoder/ms-marco-MiniLM-L6-v2` — same arch arista-mcp
ships at v0.1.0. Fine-tuning the existing weights (not training from scratch)
lets us keep the `OnnxReranker` code path identical.

**Loss:** `sentence-transformers`'s `MarginMSELoss` or
`MultipleNegativesRankingLoss`. Prefer the latter — it's simpler and matches
the triplet shape we have (one positive + N negatives per query).

**Hyperparameters (defaults in `configs/default.yaml`):**

```yaml
base_model_id: cross-encoder/ms-marco-MiniLM-L6-v2
batch_size: 32        # RTX 4070 has 12 GB VRAM, MiniLM-L6 fits comfortably
epochs: 5             # early-stop on eval plateau
lr: 2.0e-5
warmup_steps: 100
max_seq_length: 256   # (query, passage) concat
seed: 42
output_dir: checkpoints/v0.2.0
```

**Loop structure:**

```python
def train(cfg: TrainConfig) -> Path:
    model = CrossEncoder(cfg.base_model_id, num_labels=1)
    train_ds, eval_ds = load_triples(cfg.triples_path, split_seed=cfg.seed)
    loss_fn = MultipleNegativesRankingLoss(model)
    model.fit(
        train_dataloader=DataLoader(train_ds, batch_size=cfg.batch_size, shuffle=True),
        evaluator=make_mrr10_evaluator(eval_ds),
        epochs=cfg.epochs,
        warmup_steps=cfg.warmup_steps,
        learning_rate=cfg.lr,
        output_path=cfg.output_dir,
        show_progress_bar=True,
    )
    return Path(cfg.output_dir)
```

**Gate:** eval MRR@10 after training ≥ eval MRR@10 of the base checkpoint
+ 0.05 absolute.

**Wall-time estimate:** ~1.5-2 h on RTX 4070 for 1000 triples × 5 epochs.

---

## Task 9.3: ONNX export

**File:** `src/arista_reranker_tune/export.py`

```python
def export_onnx(checkpoint_dir: Path, out_dir: Path) -> None:
    # optimum-cli handles the graph tracing + dynamic axes for us.
    subprocess.run([
        "uv", "run", "optimum-cli", "export", "onnx",
        "--model", str(checkpoint_dir),
        "--task", "text-classification",
        "--opset", "17",
        str(out_dir),
    ], check=True)
    # sentence-transformers writes vocab.txt + tokenizer_config.json alongside
    # the model; we only need vocab.txt for the .NET BertWordPieceTokenizer.
    shutil.copy(checkpoint_dir / "vocab.txt", out_dir / "vocab.txt")
```

**Consumer side (no code change needed):** `arista-mcp` reads
`models/reranker/model.onnx` + `models/reranker/vocab.txt`. Copy the export
output into that dir and the next `arista-mcp serve` or `bench` run picks it up.

**Gate:** `OnnxReranker` constructs without exception on the tuned model;
a single forward pass matches the Python reference within 1e-3.

---

## Task 9.4: Bench on tuned weights

**Flow:**

```bash
# On the arista-mcp repo, with tuned ONNX in place:
cp ../arista-reranker-tune/checkpoints/v0.2.0-onnx/model.onnx models/reranker/
cp ../arista-reranker-tune/checkpoints/v0.2.0-onnx/vocab.txt   models/reranker/

# Warm bench (no history):
dotnet run --project src/AristaMcp.Cli -- bench --limit 10

# Official bench with history:
dotnet run --project src/AristaMcp.Cli -- bench \
  --history tests/fixtures/bench-history.jsonl \
  --label v0.2.0-finetuned-reranker
```

**Gate:** top-10 ≥ 95 % AND top-1 ≥ `v0.1.4-full-corpus-crlf`'s top-1 + 5 pp.

If the gate fails by a small margin: iterate on augmentation (Task 9.0)
before re-training. If it fails by > 10 pp: suspect train/eval leakage or
a tokenizer mismatch and file an issue.

---

## Task 9.5: Regression guard in `tests/`

**New fixture:** `tests/fixtures/reranker-eval-split.jsonl` — the 20 %
held-out split from Task 9.0, pinned so future retrainings are evaluated
on the exact same queries.

**New test:** `tests/AristaMcp.Embedding.Tests/OnnxRerankerEvalTest.cs`
— SkippableFact guarded on presence of `models/reranker/model.onnx`.

```csharp
[SkippableFact]
public void Reranker_OnFixedEvalSplit_MatchesExpectedMrr()
{
    Skip.IfNot(File.Exists("models/reranker/model.onnx"));
    var expected = 0.70; // floor; actual run records into bench-history
    var actual = ComputeMrrAt10(LoadEvalSplit(), new OnnxReranker(...));
    actual.Should().BeGreaterOrEqualTo(expected, "regressed from v0.2.0 baseline");
}
```

**Gate:** test passes on the v0.2.0 checkpoint; fails deliberately if the
reranker model file is replaced by a degraded one.

---

## Task 9.6: Docs

**Files:**
- `arista-mcp/docs/reranker-fine-tune.md` — end-to-end recipe: repo clone,
  data export from curate-triples, training commands, ONNX copy, bench.
- `arista-mcp/CLAUDE.md` Sprint 9 block — record which base model, which
  loss, pinned versions. Future contributors should be able to retrain on
  a fresher base model by flipping a single config line.
- `arista-reranker-tune/README.md` — self-contained; the training repo
  should be usable by someone who never touched arista-mcp.

---

## Task 9.7: `v0.2.0` release

- `CHANGELOG.md` entry: bench deltas (top-1, top-10, MRR@10, p50, p95),
  base model, training data size, reproducibility seed.
- `git tag -a v0.2.0`. Release notes should include:
  1. The benchmark row `v0.2.0-finetuned-reranker`.
  2. The training repo commit SHA that produced the shipped weights.
  3. The triples JSONL SHA (so a future rebuild can verify dataset
     identity).
- Drop `models/reranker/v0.1.0-stock.onnx` as a side-by-side asset for
  rollback; Windows release archive bundles both.

---

## Risks + mitigations

| Risk | Mitigation |
|------|------------|
| **Overfit** to the 111-query bench set — tuned model aces bench, regresses on unseen queries. | 80/20 eval split plus augmentation; if confidence drops, post-ship A/B on real Claude usage. |
| **Tokenizer drift** — ONNX export uses a different tokenizer than `BertWordPieceTokenizer` on .NET. | Export keeps the original `vocab.txt`; the .NET side doesn't touch tokenisation code. Regression test 9.5 catches any drift. |
| **VRAM OOM during training** | MiniLM-L6 is tiny (22 MB fp32). Batch 32 × seq 256 fits well under 12 GB. Fall back to batch 16 if gradient accumulation is needed. |
| **CPU serve latency regression** — tuned model runs slower than stock. | Unlikely: same architecture. But bench p95 is in the DoD; gate will catch it. |
| **Training data quality** — curate-triples pulls from a DB with noise. | Manual spot-check: sample 50 triples, label positive/negative correctness, target ≥ 90 % agreement. |

## Out-of-scope (Sprint 10+)

- Distillation to a smaller student model (e.g. MiniLM-L2) for
  sub-100 ms CPU rerank.
- Online learning — tuning on real Claude-issued queries with user
  feedback. Needs a feedback collection path that doesn't exist.
- Multi-lingual reranker (Russian EOS docs). Would start from a different
  base model.
- Ensemble reranking (tuned + stock vote) — diminishing returns at this
  corpus size.
