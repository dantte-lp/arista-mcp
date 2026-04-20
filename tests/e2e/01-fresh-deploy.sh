#!/usr/bin/env bash
# E2E-1: Fresh deploy scenario.
#   volume reset → compose up → extensions present → models fetched →
#   EF migrations applied → chunks table has bm25v column + BM25 index + triggers.
#
# Idempotent: blows away the podman volume first. Safe to re-run.
# Exit 0 on success; non-zero on any failure.

source "$(dirname "$0")/lib/common.sh"

step "01: fresh deploy"

step "1. reset compose volume"
compose down -v >/dev/null
compose up -d postgres >/dev/null
wait_for_pg 60

step "2. verify extensions"
for ext in vector vchord vchord_bm25 pg_tokenizer pg_trgm; do
  got="$(psql_q "SELECT 1 FROM pg_extension WHERE extname='$ext'")"
  [[ "$got" == "1" ]] && ok "extension $ext present" || fail "extension $ext missing"
done

step "3. fetch models"
if ! command -v pwsh >/dev/null 2>&1; then
  warn "pwsh not installed — skipping model fetch (assumes models/ already populated)"
else
  (cd "$REPO_ROOT" && pwsh scripts/fetch-models.ps1 >/dev/null)
  ok "models fetched"
fi

step "4. dotnet ef database update"
(cd "$REPO_ROOT" && dotnet build src/AristaMcp.Data --nologo >/dev/null)
(cd "$REPO_ROOT" && \
  ARISTA_MCP_CS="Host=$PGHOST;Port=$PGPORT;Database=$PGDATABASE;Username=$PGUSER;Password=$PGPASSWORD" \
  dotnet ef database update \
    --project src/AristaMcp.Data \
    --startup-project src/AristaMcp.Data >/dev/null)
ok "migrations applied"

step "5. verify chunks schema"
# bm25v column present
got="$(psql_q "SELECT 1 FROM information_schema.columns WHERE table_name='chunks' AND column_name='bm25v'")"
[[ "$got" == "1" ]] && ok "chunks.bm25v column present" || fail "chunks.bm25v column missing"

# BM25 index present
got="$(psql_q "SELECT 1 FROM pg_indexes WHERE tablename='chunks' AND indexname='idx_chunks_bm25'")"
[[ "$got" == "1" ]] && ok "idx_chunks_bm25 index present" || fail "idx_chunks_bm25 missing"

# HNSW index present
got="$(psql_q "SELECT 1 FROM pg_indexes WHERE tablename='chunks' AND indexname='IX_chunks_embedding'")"
[[ "$got" == "1" ]] && ok "HNSW embedding index present" || fail "HNSW embedding index missing"

# Two BM25 trigger rows
got="$(psql_q "SELECT count(*) FROM pg_trigger WHERE tgrelid='chunks'::regclass AND tgname LIKE 'model_chunks_model%'")"
[[ "$got" == "2" ]] && ok "2 BM25 triggers present" || fail "expected 2 BM25 triggers, got $got"

printf '\n%s%sE2E-1 passed%s\n' "$C_GREEN" "$C_BOLD" "$C_RESET" >&2
