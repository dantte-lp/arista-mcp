#!/usr/bin/env bash
# E2E-2: Category-slice ingest smoke.
#   Ingests `--category manual` (225 docs), verifies chunks + bm25v populated,
#   re-ingests without --force (must skip via catalog SHA early-exit).
#
# Precondition: E2E-1 passed (schema exists, models present, catalog at
#   ../arista-docs/data/catalog.json). Can run against a non-empty DB; this
#   test runs an isolated `--category manual` ingest to limit scope.
#
# Exit 0 on success; non-zero on any failure.

source "$(dirname "$0")/lib/common.sh"

CATALOG_PATH="${ARISTA_DOCS_CATALOG:-$REPO_ROOT/../arista-docs/data/catalog.json}"

step "02: category-slice ingest"

[[ -f "$CATALOG_PATH" ]] || fail "catalog not found at $CATALOG_PATH (override via ARISTA_DOCS_CATALOG)"

before_docs="$(psql_q "SELECT count(*) FROM documents WHERE category='manual'")"
before_chunks="$(psql_q "SELECT count(*) FROM chunks")"
step "before: documents(manual)=$before_docs chunks=$before_chunks"

step "1. ingest --category manual --force"
(cd "$REPO_ROOT" && dotnet build --nologo >/dev/null)
arista_mcp ingest --catalog "$CATALOG_PATH" --category manual --force --verbose >/dev/null

step "2. post-ingest row counts"
assert_scalar_gte "SELECT count(*) FROM documents WHERE category='manual'" 200 "documents(manual)"
assert_scalar_eq "SELECT count(*) FROM chunks WHERE bm25v IS NULL" 0 "chunks with null bm25v"

got_chunks="$(psql_q "SELECT count(*) FROM chunks")"
(( got_chunks > before_chunks )) && ok "chunks grew: $before_chunks → $got_chunks" \
  || fail "chunks did not grow after ingest (before=$before_chunks after=$got_chunks)"

step "3. re-run without --force → must skip"
last_before="$(psql_q "SELECT status FROM ingest_runs ORDER BY started_at DESC LIMIT 1")"
arista_mcp ingest --catalog "$CATALOG_PATH" --category manual >/dev/null
last_after="$(psql_q "SELECT status FROM ingest_runs ORDER BY started_at DESC LIMIT 1")"

[[ "$last_after" == "skipped" ]] && ok "re-ingest status=skipped (catalog SHA early-exit)" \
  || fail "expected status=skipped on re-ingest, got: $last_after (previous run: $last_before)"

printf '\n%s%sE2E-2 passed%s\n' "$C_GREEN" "$C_BOLD" "$C_RESET" >&2
