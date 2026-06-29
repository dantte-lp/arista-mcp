#!/usr/bin/env bash
# Dump the ingested arista corpus from a running Postgres container and
# upload it as an asset to a GitHub release.
#
# Idempotent: if the release already has an asset with a matching SHA-256
# the upload is skipped. `--clobber` is passed to `gh release upload` so
# the second-run path (same tag, regenerated dump) replaces cleanly.
#
# Usage:
#   scripts/dump-corpus.sh v0.3.0
#   scripts/dump-corpus.sh v0.3.0 --container my-pg --no-upload
#
# Requires: podman (or docker, set RUNTIME=docker), pg_dump 18+ inside
# the container, gh CLI authenticated against dantte-lp/arista-mcp.

set -euo pipefail

TAG="${1:-}"
if [[ -z "$TAG" ]]; then
  echo "usage: $0 <release-tag> [--container <name>] [--no-upload] [--output <dir>]" >&2
  exit 2
fi
shift

CONTAINER="arista-mcp-postgres"
UPLOAD=1
OUTDIR=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --container) CONTAINER="$2"; shift 2 ;;
    --no-upload) UPLOAD=0; shift ;;
    --output)    OUTDIR="$2"; shift 2 ;;
    *) echo "unknown flag: $1" >&2; exit 2 ;;
  esac
done

RUNTIME="${RUNTIME:-podman}"
DB="${DB:-arista}"
USER_NAME="${USER_NAME:-arista}"

if ! command -v "$RUNTIME" >/dev/null 2>&1; then
  echo "error: '$RUNTIME' not found in PATH (set RUNTIME=docker to override)" >&2
  exit 3
fi

if ! "$RUNTIME" container exists "$CONTAINER" 2>/dev/null && \
   ! "$RUNTIME" ps --filter "name=^${CONTAINER}$" --quiet | grep -q .; then
  echo "error: container '$CONTAINER' not found — start it before dumping" >&2
  exit 4
fi

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
OUTDIR="${OUTDIR:-$REPO_ROOT/publish}"
mkdir -p "$OUTDIR"

DUMP="$OUTDIR/arista-corpus-${TAG}.dump"
SHA="${DUMP}.sha256"

echo "[dump] running pg_dump inside ${CONTAINER}"
echo "[dump] db=${DB} user=${USER_NAME} -> ${DUMP}"

# pg_dump custom format, gzip-6 (matches the v0.3.0 manual run).
# PGPASSWORD must come from the caller's env; we never bake it into the
# command line.
PGPASSWORD="${PGPASSWORD:-${USER_NAME}}" \
  "$RUNTIME" exec -e PGPASSWORD -i "$CONTAINER" \
  pg_dump -Fc -Z 6 -U "$USER_NAME" -d "$DB" \
  > "$DUMP"

# Local SHA-256 — written next to the dump so the release asset can be
# verified without re-downloading the dump itself.
if command -v sha256sum >/dev/null 2>&1; then
  sha256sum "$DUMP" > "$SHA"
elif command -v shasum >/dev/null 2>&1; then
  shasum -a 256 "$DUMP" > "$SHA"
else
  echo "warn: no sha256sum/shasum on PATH — skipping checksum file" >&2
  rm -f "$SHA"
fi

DUMP_SIZE="$(stat -c %s "$DUMP" 2>/dev/null || stat -f %z "$DUMP")"
echo "[dump] wrote ${DUMP} (${DUMP_SIZE} bytes)"
if [[ -s "$SHA" ]]; then
  echo "[dump] $(cat "$SHA")"
fi

if [[ "$UPLOAD" -eq 0 ]]; then
  echo "[dump] --no-upload — done."
  exit 0
fi

if ! command -v gh >/dev/null 2>&1; then
  echo "error: 'gh' CLI not found — install or pass --no-upload" >&2
  exit 5
fi

# Idempotency: if the release already has an asset with the same sha256,
# skip the upload. `gh release view` returns the digest in `assets[].digest`
# as `sha256:<hex>` when GitHub finished post-processing.
EXPECTED="sha256:$(awk '{print $1}' "$SHA" 2>/dev/null || true)"
if [[ -n "$EXPECTED" && "$EXPECTED" != "sha256:" ]]; then
  EXISTING="$(gh release view "$TAG" -R dantte-lp/arista-mcp \
    --json assets --jq ".assets[]
      | select(.name == \"$(basename "$DUMP")\")
      | .digest" 2>/dev/null || true)"
  if [[ "$EXISTING" == "$EXPECTED" ]]; then
    echo "[dump] release asset already at ${EXPECTED} — skipping upload."
    exit 0
  fi
fi

echo "[dump] uploading to release ${TAG}"
gh release upload "$TAG" "$DUMP" "$SHA" --clobber -R dantte-lp/arista-mcp

echo "[dump] done."
