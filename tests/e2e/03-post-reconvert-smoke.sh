#!/usr/bin/env bash
# E2E-3: Post-reconvert smoke — promotion gate for v0.1.4 → v0.1.4 (from rc).
#
# Run AFTER:
#   1. arista-docs purge-fakes                    # wipes placeholder rows
#   2. arista-docs sync (no --fake-converter)     # real Marker on 2200+ PDFs
#   3. arista-docs catalog                        # regenerates catalog.json
#
# This script then:
#   - confirms the catalog has no placeholders left
#   - runs a fresh `arista-mcp ingest --force` against the real corpus
#   - asserts chunk count grew to the expected post-CRLF-fix scale
#   - runs `arista-mcp bench` with the v0.1.4 label and asserts the DoD gates:
#       top-10 hit rate ≥ 90 %, p95 latency ≤ 1.2 s CPU
#   - runs `arista-mcp curate-triples` and asserts ≥ 500 triples
#
# Exit 0 → all gates met; safe to promote tag. Non-zero → investigate.
#
# Requires: jq on PATH (for bench / triples JSONL parsing).

source "$(dirname "$0")/lib/common.sh"

CATALOG_PATH="${ARISTA_DOCS_CATALOG:-$REPO_ROOT/../arista-docs/data/catalog.json}"
BENCH_QUERIES="${ARISTA_BENCH_QUERIES:-$REPO_ROOT/tests/fixtures/bench-queries.json}"
BENCH_HISTORY="${ARISTA_BENCH_HISTORY:-$REPO_ROOT/tests/fixtures/bench-history.jsonl}"
BENCH_LABEL="${ARISTA_BENCH_LABEL:-v0.1.4-full-corpus-crlf}"
TRIPLES_OUT="${ARISTA_TRIPLES_OUT:-$REPO_ROOT/tests/fixtures/reranker-triples.jsonl}"

# Gate thresholds. Tuned from the Sprint 8 DoD; keep in lockstep with
# docs/superpowers/plans/2026-04-23-arista-mcp-sprint-8.md.
MIN_TOP10_PCT="${ARISTA_MIN_TOP10:-90}"
MAX_P95_MS="${ARISTA_MAX_P95_MS:-1200}"
MIN_TRIPLES="${ARISTA_MIN_TRIPLES:-500}"
MIN_EXPECTED_CHUNKS="${ARISTA_MIN_CHUNKS:-25000}"

command -v jq >/dev/null 2>&1 || fail "jq not on PATH (install: winget install jqlang.jq | apt-get install jq)"
[[ -f "$CATALOG_PATH" ]] || fail "catalog not found at $CATALOG_PATH (override via ARISTA_DOCS_CATALOG)"
[[ -f "$BENCH_QUERIES" ]] || fail "bench queries not found at $BENCH_QUERIES"

step "03: post-reconvert smoke — DoD gates for v0.1.4"

# --- 1. Catalog health --------------------------------------------------------
step "1. catalog sanity: zero placeholder conversions"

PLACEHOLDER_COUNT=$(jq '[.documents[] | select(.convert_mode == "fake")] | length' "$CATALOG_PATH")
if [[ "$PLACEHOLDER_COUNT" == "0" ]]; then
  ok "catalog placeholders: 0 (purge-fakes + real reconversion completed)"
else
  fail "catalog still contains $PLACEHOLDER_COUNT placeholder conversions — run purge-fakes + sync"
fi

# Also flag suspiciously tiny MDs — belt-and-braces for legacy entries where
# convert_mode was stamped 'accurate' by a pre-v0.1.4 FakeConverter.
TINY_MDS=$(jq '[.documents[] | select((.section_count // 0) == 0 and .category != "reference")] | length' "$CATALOG_PATH")
(( TINY_MDS < 10 )) \
  && ok "zero-section docs: $TINY_MDS (< 10 allowed — some real docs legitimately have 1 section)" \
  || warn "zero-section docs: $TINY_MDS — sanity-check with arista-docs status --stale-mds"

# --- 2. Fresh ingest into arista-mcp ------------------------------------------
step "2. fresh ingest: arista-mcp ingest --force"

wait_for_pg 30

BEFORE_CHUNKS="$(psql_q "SELECT count(*) FROM chunks")"
step "before: chunks=$BEFORE_CHUNKS"

(cd "$REPO_ROOT" && dotnet build --nologo >/dev/null)
arista_mcp ingest --catalog "$CATALOG_PATH" --force --verbose >/dev/null

AFTER_CHUNKS="$(psql_q "SELECT count(*) FROM chunks")"
step "after: chunks=$AFTER_CHUNKS"

assert_scalar_gte "SELECT count(*) FROM chunks" "$MIN_EXPECTED_CHUNKS" "total chunks"
assert_scalar_eq "SELECT count(*) FROM chunks WHERE bm25v IS NULL" 0 "chunks with null bm25v"

# No placeholder garbage in BM25. If any chunk contains the FakeConverter
# literal, the upstream fix didn't propagate OR the defensive filter in
# IngestService regressed.
FAKE_CHUNKS="$(psql_q "SELECT count(*) FROM chunks WHERE content LIKE 'Fake conversion of%'")"
if [[ "$FAKE_CHUNKS" == "0" ]]; then
  ok "zero 'Fake conversion of%' chunks in BM25 index"
else
  fail "$FAKE_CHUNKS placeholder chunks leaked into BM25 — check IngestService filter + arista-docs convert_mode"
fi

# --- 3. Bench gate ------------------------------------------------------------
step "3. bench gate: top-10 >= ${MIN_TOP10_PCT}%, p95 <= ${MAX_P95_MS} ms"

arista_mcp bench \
  --queries "$BENCH_QUERIES" \
  --limit 10 \
  --history "$BENCH_HISTORY" \
  --label "$BENCH_LABEL" >/dev/null

# Pull the row we just appended — it's the last line in the history JSONL.
LAST_ROW=$(tail -n 1 "$BENCH_HISTORY")
[[ -n "$LAST_ROW" ]] || fail "bench did not append a history row"

TOP10=$(jq -r '.topk_hit_rate' <<< "$LAST_ROW")
P95=$(jq -r '.latency_p95_ms' <<< "$LAST_ROW")
step "bench result: top-10=${TOP10}%  p95=${P95} ms"

awk -v got="$TOP10" -v min="$MIN_TOP10_PCT" 'BEGIN { exit !(got >= min) }' \
  && ok "top-10 gate: ${TOP10}% >= ${MIN_TOP10_PCT}%" \
  || fail "top-10 gate: ${TOP10}% < ${MIN_TOP10_PCT}%"

awk -v got="$P95" -v max="$MAX_P95_MS" 'BEGIN { exit !(got <= max) }' \
  && ok "p95 gate: ${P95} ms <= ${MAX_P95_MS} ms" \
  || fail "p95 gate: ${P95} ms > ${MAX_P95_MS} ms"

# --- 4. Triples gate ----------------------------------------------------------
step "4. triples gate: curate-triples emits >= $MIN_TRIPLES rows"

arista_mcp curate-triples \
  --queries "$BENCH_QUERIES" \
  --out "$TRIPLES_OUT" \
  --negatives-per-query 4 >/dev/null

TRIPLE_COUNT=$(wc -l < "$TRIPLES_OUT" | tr -d ' ')
step "triples: $TRIPLE_COUNT"

(( TRIPLE_COUNT >= MIN_TRIPLES )) \
  && ok "triples gate: $TRIPLE_COUNT >= $MIN_TRIPLES" \
  || fail "triples gate: $TRIPLE_COUNT < $MIN_TRIPLES — retrieval diversity may be too low"

# --- Summary ------------------------------------------------------------------
printf '\n%s%sE2E-3 passed — all v0.1.4 DoD gates met%s\n' "$C_GREEN" "$C_BOLD" "$C_RESET" >&2
printf '  next: git tag -a v0.1.4 -m "..." && git tag -d v0.1.4-rc1\n' >&2
