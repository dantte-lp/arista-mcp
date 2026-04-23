#!/usr/bin/env bash
# Shared helpers for arista-mcp E2E shell scenarios.
# Source at the top of every e2e/*.sh:   source "$(dirname "$0")/lib/common.sh"

set -euo pipefail

# --- colours ---------------------------------------------------------------
if [[ -t 2 ]]; then
  C_RED=$'\033[31m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'
  C_CYAN=$'\033[36m'; C_BOLD=$'\033[1m'; C_RESET=$'\033[0m'
else
  C_RED=''; C_GREEN=''; C_YELLOW=''; C_CYAN=''; C_BOLD=''; C_RESET=''
fi

step() { printf '%s▸ %s%s\n' "$C_BOLD" "$*" "$C_RESET" >&2; }
ok()   { printf '%s✓ %s%s\n'  "$C_GREEN" "$*" "$C_RESET" >&2; }
warn() { printf '%s! %s%s\n'  "$C_YELLOW" "$*" "$C_RESET" >&2; }
fail() { printf '%s✗ %s%s\n'  "$C_RED" "$*" "$C_RESET" >&2; exit 1; }

# --- paths -----------------------------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/docker/compose.yaml"
export PGHOST="${PGHOST:-localhost}"
export PGPORT="${PGPORT:-5434}"
export PGUSER="${PGUSER:-arista}"
export PGDATABASE="${PGDATABASE:-arista}"
export PGPASSWORD="${PGPASSWORD:-arista}"
ARISTA_PG_CONTAINER="${ARISTA_PG_CONTAINER:-arista-mcp-postgres}"

# --- compose wrapper -------------------------------------------------------
compose() {
  if command -v podman-compose >/dev/null 2>&1; then
    podman-compose -f "$COMPOSE_FILE" "$@"
  elif command -v podman >/dev/null 2>&1; then
    podman compose -f "$COMPOSE_FILE" "$@"
  else
    docker compose -f "$COMPOSE_FILE" "$@"
  fi
}

# --- psql helpers ----------------------------------------------------------
# Runs a query inside the postgres container so we don't need psql on the host.
psql_q() {
  local sql="$1"
  podman exec "$ARISTA_PG_CONTAINER" \
    psql -U "$PGUSER" -d "$PGDATABASE" -tAc "$sql"
}

assert_scalar_eq() {
  local sql="$1" expected="$2" label="${3:-query}"
  local got
  got="$(psql_q "$sql")"
  if [[ "$got" == "$expected" ]]; then
    ok "$label: $got"
  else
    fail "$label: expected '$expected', got '$got'"
  fi
}

assert_scalar_gte() {
  local sql="$1" minimum="$2" label="${3:-query}"
  local got
  got="$(psql_q "$sql")"
  if (( got >= minimum )); then
    ok "$label: $got (≥ $minimum)"
  else
    fail "$label: expected ≥ $minimum, got $got"
  fi
}

# --- waiters ---------------------------------------------------------------
wait_for_pg() {
  local timeout="${1:-60}" elapsed=0
  step "waiting for $ARISTA_PG_CONTAINER to accept connections"
  while (( elapsed < timeout )); do
    if podman exec "$ARISTA_PG_CONTAINER" pg_isready -U "$PGUSER" -d "$PGDATABASE" >/dev/null 2>&1; then
      ok "postgres ready after ${elapsed}s"
      return 0
    fi
    sleep 2
    elapsed=$((elapsed + 2))
  done
  fail "postgres not ready after ${timeout}s"
}

# --- CLI wrapper -----------------------------------------------------------
# DOTNET_CONFIG override routes to Release when the tree was built that way
# (e.g. the GPU-ingest flow builds Release to minimise CUDA overhead). Default
# stays Debug to match the plain `dotnet build` in the other scripts.
arista_mcp() {
  local cfg="${DOTNET_CONFIG:-Debug}"
  (cd "$REPO_ROOT" && dotnet run --project src/AristaMcp.Cli --no-build -c "$cfg" -- "$@")
}
