# arista-mcp Sprint 4 Implementation Plan — Polish + v0.1.0 Release

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans.

**Goal:** Land the three critical bugs surfaced by the post-Sprint-3 code review, adopt the high-value C# 14 / .NET 10 modernizations, ship the `OnnxReranker` deferred from Sprint 3, polish the operator surface (README, benchmarks, real-catalog smoke), and cut `v0.1.0`.

**Reference spec:** `docs/superpowers/specs/2026-04-16-arista-mcp-design.md`.
**Sprint 1 plan:** `docs/superpowers/plans/2026-04-16-arista-mcp-implementation.md`.
**Sprint 2 plan:** `docs/superpowers/plans/2026-04-17-arista-mcp-sprint-2.md`.
**Sprint 3 plan:** `docs/superpowers/plans/2026-04-17-arista-mcp-sprint-3.md`.
**Code review:** in session history (2026-04-20).

---

## Sprint 4 Overview

| # | Task | Priority | Outcome |
|---|------|---------|---------|
| 4.1 | Fix `Bm25Score` diagnostic bug | 🔴 critical | retriever reports real BM25 score on dense+sparse co-hits |
| 4.2 | Fix missing dense/sparse query timings | 🔴 critical | `SearchDiagnostics.DenseQueryMs` + `SparseQueryMs` populated |
| 4.3 | Wire bm25v manual SQL into EF migration | 🔴 critical | `dotnet ef database update` on a fresh DB provisions bm25v + trigger + index |
| 4.4 | C# 14 / .NET 10 modernizations (H1–H4) | 🟡 high-value | FrozenDictionary, CollectionsMarshal, TensorPrimitives, span-y casts |
| 4.5 | `TimeProvider` injection (H5) | 🟡 high-value | testable ingest-run clocks |
| 4.6 | Dead code + naming cleanup | 🟢 minor | no `_ = jsonFull`, consolidated Null/Noop types, `sealed` entities |
| 4.7 | `OnnxReranker` (bge-reranker-base) | ⭐ feature | cross-encoder rerank as drop-in over NoopReranker |
| 4.8 | Safe version bumps | 🔵 hygiene | Spectre.Console, Meziantou.Analyzer, System.Security.Cryptography.Xml |
| 4.9 | Retrieval benchmark harness | ⭐ feature | 30-query smoke over real arista-docs catalog, latency + nDCG@10 |
| 4.10 | README.md | ⭐ feature | user-facing setup / usage / architecture one-pager |
| 4.11 | v0.1.0 release gate | 🏁 gate | full test run, smoke ingest on real catalog, `git tag v0.1.0` |

**Definition of Done:**
- [ ] All three critical bugs fixed with regression tests.
- [ ] `dotnet test` all green (Sprint 1–3 + new Sprint 4 tests).
- [ ] `dotnet ef database update` on a fresh container provisions the complete schema (bm25v + index + trigger) without manual SQL.
- [ ] `arista-mcp bench` runs a 30-query smoke test and emits p50/p95 latency + top-1 presence stats.
- [ ] README covers: prereqs, fetch-models, ingest, serve (stdio + HTTP), connect from a Claude client.
- [ ] `git tag v0.1.0` exists; `CHANGELOG.md` lists all four sprints.
- [ ] Reranker either ships enabled (with model downloaded) or falls back to NoopReranker cleanly.

---

## Task 4.1: Fix `Bm25Score` for dense+sparse co-hits

**Files:**
- Modify: `src/AristaMcp.Server/Retrieval/HybridRetriever.cs`
- Add: `tests/AristaMcp.Data.Tests/Retrieval/HybridRetrieverDiagnosticsTest.cs`

**Steps:**

1. **Write the failing test first**: seed a small corpus where one chunk reliably scores on both dense and sparse sides. Assert that `result.Bm25Score` is NOT `-cosine_distance` — cosine distance is `[0, 2]`, BM25's `<&>` returns negative scores (higher |score| = better match), typically in the `[-20, 0]` range. Test: `Bm25Score` must be ≤ 0 and != -DenseSimilarity for the co-hit chunk.
2. **Restructure `FusedCandidate`** to carry both sides' distances explicitly:
   ```csharp
   private sealed record FusedCandidate(
       CandidateRow Row,
       float RrfScore,
       int? DenseRank,
       int? SparseRank,
       float? DenseDistance,
       float? SparseDistance);
   ```
3. **Update `ReciprocalRankFusion`** to track dense + sparse distance in the accumulator separately, not rely on `Row.Distance`.
4. **Update `Build`** to compute `DenseSimilarity = 1 - DenseDistance` (cosine similarity) and `Bm25Score = -SparseDistance`.
5. **Run** — test passes.

## Task 4.2: Wire real timings for dense/sparse SQL

**Files:**
- Modify: `src/AristaMcp.Server/Retrieval/HybridRetriever.cs`
- Add: test `HybridRetrieverDiagnosticsTest.Timings_AreNonZero` (same file as 4.1).

**Steps:**

1. Change `RunDenseAsync` / `RunSparseAsync` to return `(List<CandidateRow>, double Elapsed)`.
2. Each wraps its work in a `Stopwatch`.
3. `SearchAsync` unpacks both and threads the values into `SearchDiagnostics`.
4. Test asserts both > 0 for a fresh query.

## Task 4.3: Wire `bm25v` SQL into the EF migration

**Files:**
- Modify: `src/AristaMcp.Data/AristaDbContext.cs` OR add a new EF migration
- Delete: `src/AristaMcp.Data/Migrations/Manual/` (after its content moves)
- Modify: `tests/AristaMcp.Data.Tests/Fixtures/PgvectorFixture.cs` — drop `ApplyBm25Async` since EF will handle it.

**Option A (preferred): new EF migration with raw SQL**

1. `dotnet ef migrations add AddBm25Column --project src/AristaMcp.Data --startup-project src/AristaMcp.Data`
2. Hand-edit the generated `Up()` to `migrationBuilder.Sql(@"…")` with the three statements from `001_bm25v_column.sql`. `Down()` drops them.
3. Delete the `Migrations/Manual/` folder.
4. Update `PgvectorFixture` — `ctx.Database.MigrateAsync()` now provisions everything; remove the manual file-read step.

**Option B: `IDatabaseStarter` runtime provisioner** — rejected; runtime DDL is surprising and breaks production safety.

**Test:** `PgvectorFixture` still passes; `Bm25IndexSearchTest` still passes.

## Task 4.4: C# 14 / .NET 10 modernizations

**H1 — `QueryExpander.Synonyms` → `FrozenDictionary`:**
```csharp
private static readonly FrozenDictionary<string, string> Synonyms =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["EVPN"] = "Ethernet VPN",
        // …
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
```
No behavioural change, existing tests still pass.

**H2 — `HybridRetriever.ReciprocalRankFusion` → `CollectionsMarshal.GetValueRefOrAddDefault`:** combine with 4.1's struct refactor so this is one atomic rewrite.

**H3 — `OnnxEmbedder.ExtractAndNormalize` → `TensorPrimitives`:**
```csharp
using System.Numerics.Tensors;
// inside ExtractAndNormalize
var vec = new float[HiddenSize];
sentence.Slice(batchIdx * HiddenSize, HiddenSize).CopyTo(vec);
var norm = TensorPrimitives.Norm(vec);
if (norm > 1e-9f) TensorPrimitives.Divide(vec, norm, vec);
return vec;
```
Add `System.Numerics.Tensors` package reference to Embedding csproj if not already implicit.

**H4 — collection-expression `Half[]` casts:** two call sites, `ChunkRepository.BulkInsertAsync` line 72-76 and `HybridRetriever.SearchAsync` line 39. Pattern:
```csharp
Half[] halfArr = [.. c.Embedding.Select(static f => (Half)f)];
```

**Test:** all existing tests pass; no new tests required (behaviour unchanged).

## Task 4.5: `TimeProvider` injection

**Files:**
- Modify: `src/AristaMcp.Data/Repositories/IngestRunRepository.cs`, `DocumentRepository.cs`
- Modify: `src/AristaMcp.Server/ServerHosting.cs` — register `TimeProvider.System` as singleton
- Modify: `tests/AristaMcp.Data.Tests/IngestRunRepositoryTest.cs` — pass `TimeProvider.System` (or a fake for determinism)

Inject `TimeProvider` via ctor, replace `DateTimeOffset.UtcNow` → `timeProvider.GetUtcNow()`. DI registers `TimeProvider.System` by default. Tests keep using real time; adding a `FakeTimeProvider` is deferred to Sprint 5.

## Task 4.6: Dead code + naming cleanup

**Steps:**
1. `DocumentLoader.cs:31` — remove `_ = jsonFull;` and the misleading comment. If JSON enrichment is planned for Sprint 5, track it in the plan, not as dead code here.
2. Consolidate `IngestCommand.NoopEmbedder` → move to `src/AristaMcp.Embedding/NoopEmbedder.cs` as a reusable type (mirrors `NoopReranker` in `AristaMcp.Core.Retrieval`). Update usages.
3. Mark `DocumentEntity`, `ChunkEntity`, `IngestRunEntity`, `AristaDbContext` as `sealed` — no proxy inheritance is used.
4. `GetDocumentTool.cs:42` — deserialize `doc.TagsJson` into a `string[]` before returning, so clients get a real array.
5. Commit as `chore(review): cleanup from v0.1 code review`.

## Task 4.7: `OnnxReranker` (bge-reranker-base)

**Files:**
- Create: `src/AristaMcp.Embedding/OnnxReranker.cs`
- Create: `src/AristaMcp.Embedding/RerankerOptions.cs`
- Modify: `scripts/fetch-models.ps1` — add `/reranker/model.onnx` + `/reranker/vocab.txt` (from `BAAI/bge-reranker-base`)
- Modify: `src/AristaMcp.Server/ServerHosting.cs` — register `OnnxReranker` when `models/reranker/model.onnx` exists, else `NoopReranker`
- Create: `tests/AristaMcp.Embedding.Tests/OnnxRerankerTests.cs` (SkippableFact)

**Implementation notes:**
- bge-reranker-base is a cross-encoder (XLM-RoBERTa-base, `num_labels=1`); takes `[query, doc]` as a single tokenized sequence with `[SEP]` separator.
- `Microsoft.ML.Tokenizers 2.0.0`'s `BertTokenizer` doesn't expose `EncodeToIds(text, textPair, ...)`. Workaround: tokenize the query + tokenize the doc + concatenate `[CLS] query_ids [SEP] doc_ids [SEP]` manually. Pad to max-in-batch; mask = 1 on real tokens, 0 on padding.
- ONNX inputs: `input_ids`, `attention_mask` (int64, `[B, L]`). Output: `logits` `[B, 1]` — one raw score per pair, higher = better match.
- Batch candidates through the reranker in chunks of `BatchSize` (default 8; rerankers are heavier than embedders).

**Tests (SkippableFact, skip when `models/reranker/model.onnx` absent):**
- `RerankedOrder_PutsRelevantDocFirst` — "EVPN overlay" vs [EVPN doc, OSPF doc, cake recipe] → EVPN first.

## Task 4.8: Safe version bumps

**Files:** `Directory.Packages.props`, `Directory.Build.props`

- `Spectre.Console` 0.55.0 → 0.55.2
- `Meziantou.Analyzer` 3.0.48 → 3.0.50
- `System.Security.Cryptography.Xml` 9.0.15 → 10.0.6

Single commit `chore(deps): safe patch bumps (Spectre, Meziantou, SSC.Xml)`. `dotnet test` green.

## Task 4.9: Retrieval benchmark harness

**Files:**
- Create: `src/AristaMcp.Cli/Commands/BenchCommand.cs`
- Create: `src/AristaMcp.Cli/Benchmarks/QuerySet.cs` (30 canned queries with expected doc_id or keyword)
- Create: `tests/fixtures/bench-queries.json` — held-out query set
- Modify: `src/AristaMcp.Cli/Program.cs` — register command

**Steps:**
1. Hand-curate 30 Arista-flavoured queries with expected top-1 doc slugs (from catalog.json inspection).
2. `arista-mcp bench --catalog ../arista-docs/data/catalog.json --queries bench-queries.json` runs each query through `IHybridRetriever`, collects:
   - Per-query latency (total + per-stage via diagnostics)
   - Top-1 hit rate (did the expected doc appear in top-1?)
   - Top-10 hit rate (did it appear anywhere in top-10?)
3. Emits a Spectre table: `query | top1 | top10 | total_ms`. Summary row with p50/p95/avg.
4. Exits non-zero if top-10 hit rate < 80% (tunable).

The harness doubles as the release smoke test.

## Task 4.10: README.md

**Files:**
- Create: `README.md`

**Sections (in order):**
1. One-paragraph elevator pitch.
2. Features (hybrid retrieval, stdio + HTTP MCP, PostgreSQL 18 + pgvector + vchord_bm25).
3. Prereqs (.NET 10 SDK, Podman or Docker, PowerShell, ~4 GB disk for models + DB).
4. Quickstart:
   ```bash
   podman compose -f docker/compose.yaml up -d postgres
   pwsh scripts/fetch-models.ps1
   dotnet ef database update --project src/AristaMcp.Data --startup-project src/AristaMcp.Data
   dotnet run --project src/AristaMcp.Cli -- ingest
   dotnet run --project src/AristaMcp.Cli -- serve --transport http --port 8080
   ```
5. Connecting from Claude Desktop / Claude Code (`.mcp.json` snippet).
6. Architecture diagram (ASCII layer diagram from `CLAUDE.md`).
7. Development (tests, Sprints, pins).
8. License.

## Task 4.11: v0.1.0 release gate

**Steps:**
1. Full `dotnet build` + `dotnet test` green.
2. Fresh postgres volume → `dotnet ef database update` → `arista-mcp ingest` on a 20-doc subset of the real catalog (verify chunks + bm25v populated).
3. `arista-mcp bench` over 30-query set — record numbers in `CHANGELOG.md`.
4. `arista-mcp serve --transport stdio` + `arista-mcp serve --transport http --port 8080` both boot, both list 5 tools.
5. Create `CHANGELOG.md` covering v0.1.0 (four-sprint summary, gotchas, known limitations — bge-reranker is optional, WSL2/Podman port-binding note, EF Core 9.x pin).
6. Update `CLAUDE.md` Sprint 4 additions.
7. `git tag v0.1.0`.

## Gate checklist

- [ ] Three critical bugs fixed, each with a regression test.
- [ ] `dotnet build` clean; `dotnet test` all green (Sprint 1+2+3+4).
- [ ] Fresh-DB provisioning works end-to-end without manual SQL.
- [ ] `arista-mcp bench` runs and emits the results table.
- [ ] `OnnxReranker` tests pass when model present; NoopReranker fallback verified.
- [ ] README quickstart reproduces on a clean checkout.
- [ ] `CHANGELOG.md` and `CLAUDE.md` updated.
- [ ] `v0.1.0` tag exists.
