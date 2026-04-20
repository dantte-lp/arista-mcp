# arista-mcp Sprint 6 Implementation Plan — Test Isolation + Full-Corpus Baseline

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or superpowers:executing-plans.

**Goal:** Eliminate the class of bug where running `dotnet test` TRUNCATEs a developer's
real ingest, then land the true full-corpus bench baseline on the prod `arista` DB.

**Context:** Sprint 5's release gate (Sprint 5.9) ran the full `dotnet test` suite, and
the `[Collection("Pgvector")]` integration tests called `ResetAsync()` — TRUNCATE chunks,
documents, ingest_runs CASCADE — on the same database that Sprint 5.7 had just
ingested 225 manual-category docs into. The bench baseline number was valid at the
time of capture but the dataset that produced it was wiped immediately afterwards.

---

## Sprint 6 Overview

| # | Task | Outcome |
|---|------|---------|
| 6.1 | Isolate integration tests in `arista_test` DB | `PgvectorFixture` creates + migrates a separate DB on first use; refuses to run against any DB whose name doesn't end in `_test` |
| 6.2 | Full-corpus ingest into prod `arista` DB | All 2426 docs, ~65k chunks, CPU-only run |
| 6.3 | Full-corpus bench + `v0.1.2` tag | Append baseline row to history; compare vs Sprint 5's manual-category baseline |

---

## Task 6.1: Test-DB isolation

**File:** `tests/AristaMcp.Data.Tests/Fixtures/PgvectorFixture.cs`

**Changes:**
- Default `ConnectionString` → `Database=arista_test` (was `arista`).
- `InitializeAsync` gains `EnsureTestDatabaseAsync` step that connects to the
  maintenance `postgres` DB, probes `pg_database` for the target, and issues
  `CREATE DATABASE "arista_test"` when missing.
- Guard clause: refuse to run if the target DB name doesn't end in `_test` —
  prevents accidental prod-wipe when a developer sets
  `ARISTA_MCP_TEST_CS=…Database=arista…`.
- All subsequent steps (extensions from `docker/init.sql`, EF
  `ctx.Database.MigrateAsync()`, `ResetAsync`) run against the isolated DB.

**Test:** `dotnet test` full-suite runs clean against a freshly-created
`arista_test`; `SELECT count(*) FROM documents` in prod `arista` is untouched
before and after.

## Task 6.2: Full-corpus ingest

Pre-conditions: embedder model present (`models/embedder/model.onnx`); postgres
container healthy; `arista` DB at baseline schema (EF migrations applied, no prior
data).

    arista-mcp ingest --catalog ../arista-docs/data/catalog.json --force

Budget: 15–25 min on CPU depending on corpus (2426 docs, ~32k sections). EOS-User-
Manual is the long tail (5234 pages / 17578 sections alone). ONNX Runtime pegs
`IntraOpNumThreads = Environment.ProcessorCount` so the machine will be under full
embed load throughout.

**Verification:** `ingest_runs.status = 'success'`, `docs_upserted = 2426`,
`chunks WHERE bm25v IS NULL = 0`.

## Task 6.3: Full-corpus bench + v0.1.2 tag

    arista-mcp bench \
      --queries tests/fixtures/bench-queries.json \
      --limit 10 \
      --history tests/fixtures/bench-history.jsonl \
      --label v0.1.2-full-corpus-baseline

**Expected uplift vs Sprint 5 manual-category baseline** (80% top-10, 67% top-1):
- Queries for `hardware`, `CV-CUE`, `CVA`, `CVW`, `aboot`, `OSPF-campus` now have
  their source docs present and should hit.
- Target: top-10 ≥ 95%, top-1 ≥ 80%.

Update CHANGELOG + CLAUDE.md; `git tag v0.1.2`.

## Gate

- [ ] Full `dotnet test` passes against isolated `arista_test`.
- [ ] Prod `arista` has all 2426 docs + ~65k chunks with bm25v populated.
- [ ] New bench history row shows uplift from v0.1.1 baseline.
- [ ] `v0.1.2` tag exists.
