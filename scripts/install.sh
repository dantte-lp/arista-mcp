#!/usr/bin/env bash
# One-shot install for arista-mcp — binary, ONNX models, Postgres corpus,
# MCP registration in Claude Code + Codex, and the Claude Code plugin
# (skills + slash commands). Idempotent — safe to re-run.
#
# Two entry points:
#
#   A. From a release tarball (recommended):
#        gh release download vX.Y.Z -R dantte-lp/arista-mcp \
#          -p 'arista-mcp-vX.Y.Z-linux-x64.tar.gz'
#        tar -xzf arista-mcp-vX.Y.Z-linux-x64.tar.gz
#        cd arista-mcp-vX.Y.Z-linux-x64
#        bash scripts/install.sh
#
#   B. From a git checkout:
#        git clone https://github.com/dantte-lp/arista-mcp
#        cd arista-mcp && bash scripts/install.sh
#
# The script picks the newest release automatically unless TAG=vX.Y.Z is
# in the environment. It never overwrites your local settings: existing
# entries in ~/.claude.json / ~/.codex/config.toml are merged in place.

set -euo pipefail

readonly APP=arista-mcp
readonly ENV_PREFIX=ARISTA_MCP__
readonly PG_HOST=localhost
readonly PG_PORT=5434
readonly PG_DB=arista
readonly PG_USER=arista
readonly PG_PASSWORD_DEFAULT=arista

readonly BOLD=$'\e[1m'
readonly RED=$'\e[31m'
readonly GREEN=$'\e[32m'
readonly YELLOW=$'\e[33m'
readonly CYAN=$'\e[36m'
readonly RESET=$'\e[0m'

log()  { printf '%s[install]%s %s\n' "$CYAN" "$RESET" "$*"; }
warn() { printf '%s[warn]%s %s\n' "$YELLOW" "$RESET" "$*" >&2; }
die()  { printf '%s[error]%s %s\n' "$RED" "$RESET" "$*" >&2; exit 1; }
ok()   { printf '%s[ok]%s %s\n' "$GREEN" "$RESET" "$*"; }

# ─────────────────────────────────────────────────────────────────────
# Layout: this script must sit inside the release bundle OR the repo.
# Both cases pass a bundle directory that we treat as the source of truth.
# ─────────────────────────────────────────────────────────────────────
BUNDLE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
log "bundle: $BUNDLE"

# Prefer the co-located binary (release tarball); fall back to $PATH.
if [ -x "$BUNDLE/$APP" ]; then
  SRC_BIN="$BUNDLE/$APP"
elif command -v "$APP" >/dev/null 2>&1; then
  SRC_BIN="$(command -v "$APP")"
else
  die "no $APP binary in $BUNDLE or on \$PATH"
fi
log "binary source: $SRC_BIN"

# Resolve the version once so every downstream step agrees.
TAG="${TAG:-}"
if [ -z "$TAG" ]; then
  # `X.Y.Z+sha` → v"X.Y.Z"
  TAG="v$("$SRC_BIN" --version 2>&1 | head -1 | cut -d+ -f1)"
fi
[[ "$TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+ ]] \
  || die "could not resolve release tag from binary version; set TAG=vX.Y.Z"
log "tag: $TAG"

# ─────────────────────────────────────────────────────────────────────
# 1. Binary → /usr/local/bin (versioned dir under /opt/apps for rollback)
# ─────────────────────────────────────────────────────────────────────
INSTALL_DIR="/opt/apps/${APP}-${TAG}-linux-x64"
CURRENT_LINK="/opt/apps/${APP}-current"
mkdir -p /opt/apps
if [ ! -x "$INSTALL_DIR/$APP" ]; then
  log "installing binary to $INSTALL_DIR"
  # Copy the whole bundle so deploy/, docs/, scripts/ ride along.
  mkdir -p "$INSTALL_DIR"
  cp -a "$BUNDLE"/. "$INSTALL_DIR"/
fi
ln -sfn "$INSTALL_DIR" "$CURRENT_LINK"
ln -sfn "$CURRENT_LINK/$APP" "/usr/local/bin/$APP"
INSTALLED_VERSION="$("/usr/local/bin/$APP" --version | head -1)"
ok "$APP → /usr/local/bin/$APP ($INSTALLED_VERSION)"

# ─────────────────────────────────────────────────────────────────────
# 2. ONNX models — fetch only if absent
# ─────────────────────────────────────────────────────────────────────
MODELS_DIR="${MODELS_DIR:-/var/lib/${APP}/models}"
if [ ! -f "$MODELS_DIR/embedder/model.onnx" ]; then
  log "fetching ONNX models into $MODELS_DIR"
  mkdir -p "$MODELS_DIR"
  if command -v pwsh >/dev/null 2>&1; then
    pwsh "$INSTALL_DIR/scripts/fetch-models.ps1" -ModelsRoot "$MODELS_DIR"
  else
    warn "pwsh not found; skipping automatic model fetch"
    warn "  install PowerShell 7+ or run scripts/fetch-models.ps1 by hand"
  fi
else
  ok "models present at $MODELS_DIR"
fi

# ─────────────────────────────────────────────────────────────────────
# 3. Postgres + corpus via bootstrap (idempotent — skips existing container)
#    Skip with SKIP_BOOTSTRAP=1 when the target DB is already populated
#    (e.g. re-running the installer to refresh binary / config only).
# ─────────────────────────────────────────────────────────────────────
if [ "${SKIP_BOOTSTRAP:-0}" = "1" ]; then
  log "SKIP_BOOTSTRAP=1 set — skipping postgres bootstrap"
elif command -v podman >/dev/null 2>&1 || command -v docker >/dev/null 2>&1; then
  log "bootstrapping postgres + corpus dump (set SKIP_BOOTSTRAP=1 to skip)"
  "/usr/local/bin/$APP" bootstrap --release "$TAG" || \
    warn "bootstrap non-zero — inspect above; may already be provisioned"
else
  warn "no podman/docker; skipping postgres bootstrap"
  warn "  point ${ENV_PREFIX}ConnectionString at an externally-managed PG"
fi

CONN_STRING="${CONN_STRING:-Host=$PG_HOST;Port=$PG_PORT;Database=$PG_DB;Username=$PG_USER;Password=$PG_PASSWORD_DEFAULT}"

# ─────────────────────────────────────────────────────────────────────
# 4. Claude Code — MCP server registration in ~/.claude.json
# ─────────────────────────────────────────────────────────────────────
CLAUDE_CFG="$HOME/.claude.json"
if command -v jq >/dev/null 2>&1 && [ -f "$CLAUDE_CFG" ]; then
  log "registering $APP in $CLAUDE_CFG"
  cp -a "$CLAUDE_CFG" "$CLAUDE_CFG.bak-$(date +%Y%m%d-%H%M%S)"
  tmp="$(mktemp)"
  jq --arg app "$APP" \
     --arg cs "$CONN_STRING" \
     --arg md "$MODELS_DIR" \
     '.mcpServers[$app] = {
        type: "stdio",
        command: ("/usr/local/bin/" + $app),
        args: ["serve", "--transport", "stdio"],
        env: ({
          ("'"$ENV_PREFIX"'ConnectionString"): $cs,
          ("'"$ENV_PREFIX"'ModelsDir"): $md
        })
      }' "$CLAUDE_CFG" > "$tmp" && mv "$tmp" "$CLAUDE_CFG"
  ok "Claude Code mcpServers.$APP registered"
elif [ ! -f "$CLAUDE_CFG" ]; then
  warn "$CLAUDE_CFG not found; skipping Claude Code registration"
else
  warn "jq not on \$PATH; skipping Claude Code registration"
fi

# ─────────────────────────────────────────────────────────────────────
# 5. Claude Code plugin — clone marketplace + install plugin, or symlink
# ─────────────────────────────────────────────────────────────────────
CLAUDE_PLUGIN_ROOT="$HOME/.claude/plugins"
CLAUDE_MARKETPLACE_LINK="$CLAUDE_PLUGIN_ROOT/marketplaces/$APP"
if [ -d "$CLAUDE_PLUGIN_ROOT" ] && [ ! -e "$CLAUDE_MARKETPLACE_LINK" ]; then
  log "linking Claude Code marketplace: $INSTALL_DIR → $CLAUDE_MARKETPLACE_LINK"
  mkdir -p "$CLAUDE_PLUGIN_ROOT/marketplaces"
  ln -s "$INSTALL_DIR" "$CLAUDE_MARKETPLACE_LINK"
  ok "run \`/plugin install $APP\` in Claude Code to activate slash commands + skills"
elif [ -L "$CLAUDE_MARKETPLACE_LINK" ]; then
  # Refresh link target on version bumps.
  ln -sfn "$INSTALL_DIR" "$CLAUDE_MARKETPLACE_LINK"
  ok "Claude Code marketplace already linked (refreshed target)"
fi

# ─────────────────────────────────────────────────────────────────────
# 6. Codex — MCP server registration via `codex mcp add`
# ─────────────────────────────────────────────────────────────────────
if command -v codex >/dev/null 2>&1; then
  log "registering $APP in Codex"
  codex mcp remove "$APP" 2>/dev/null || true
  codex mcp add "$APP" \
    --env "${ENV_PREFIX}ConnectionString=$CONN_STRING" \
    --env "${ENV_PREFIX}ModelsDir=$MODELS_DIR" \
    -- /usr/local/bin/"$APP" serve --transport stdio
  ok "Codex mcp_servers.$APP registered"
else
  warn "codex not on \$PATH; skipping Codex registration"
fi

# ─────────────────────────────────────────────────────────────────────
# 7. Codex skills — copy each skill under plugin/skills/ into ~/.codex/skills
# ─────────────────────────────────────────────────────────────────────
CODEX_SKILLS="$HOME/.codex/skills"
PLUGIN_SKILLS="$INSTALL_DIR/plugin/skills"
if [ -d "$PLUGIN_SKILLS" ]; then
  mkdir -p "$CODEX_SKILLS"
  for skill_dir in "$PLUGIN_SKILLS"/*/; do
    [ -d "$skill_dir" ] || continue
    skill_name="$(basename "$skill_dir")"
    dst="$CODEX_SKILLS/$skill_name"
    if [ -L "$dst" ] || [ ! -e "$dst" ]; then
      ln -sfn "$skill_dir" "$dst"
      ok "Codex skill: $skill_name"
    else
      warn "skill $skill_name exists (non-symlink); leaving as-is"
    fi
  done
fi

# ─────────────────────────────────────────────────────────────────────
# 8. Summary
# ─────────────────────────────────────────────────────────────────────
cat <<EOF

$BOLD${GREEN}$APP $TAG installed.$RESET

  binary:        /usr/local/bin/$APP
  install dir:   $INSTALL_DIR
  models:        $MODELS_DIR
  connection:    $CONN_STRING
  Claude Code:   restart the CLI or run \`/plugin install $APP\` if the
                 marketplace was just linked
  Codex:         next \`codex\` session will pick up the MCP server + skill

Quick check:

  $APP get-status || true   # not an actual verb; use tools/list via a client
  /usr/local/bin/$APP --version
  jq '.mcpServers["$APP"]' ~/.claude.json
  codex mcp list | grep $APP

EOF
