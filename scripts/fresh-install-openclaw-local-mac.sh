#!/usr/bin/env bash
set -euo pipefail

# Fresh install flow (no backup restore):
# clone -> install -> build -> start gateway if config exists
#
# Usage:
#   ./scripts/fresh-install-openclaw-local-mac.sh
#
# Optional:
#   --workspace-root /Users/jacob/githuff
#   --openclaw-repo /Users/jacob/githuff/openclaw
#   --openclaw-repo-url https://github.com/openclaw/openclaw.git

WORKSPACE_ROOT=""
OPENCLAW_REPO=""
OPENCLAW_REPO_URL="https://github.com/openclaw/openclaw.git"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RECLAW_REPO="$(cd "$SCRIPT_DIR/.." && pwd)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --workspace-root)
      WORKSPACE_ROOT="$2"
      shift 2
      ;;
    --openclaw-repo)
      OPENCLAW_REPO="$2"
      shift 2
      ;;
    --openclaw-repo-url)
      OPENCLAW_REPO_URL="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [[ -z "$WORKSPACE_ROOT" ]]; then
  WORKSPACE_ROOT="$(cd "$RECLAW_REPO/.." && pwd)"
fi

if [[ -z "$OPENCLAW_REPO" ]]; then
  OPENCLAW_REPO="$WORKSPACE_ROOT/openclaw"
fi

log() {
  echo "[fresh-install] $*"
}

log "Ensuring OpenClaw repo exists"
if [[ ! -d "$OPENCLAW_REPO/.git" ]]; then
  git clone "$OPENCLAW_REPO_URL" "$OPENCLAW_REPO"
fi

cd "$OPENCLAW_REPO"

log "Installing dependencies"
if command -v corepack >/dev/null 2>&1; then
  corepack enable >/dev/null 2>&1 || true
  corepack pnpm install
else
  pnpm install
fi

log "Building Control UI assets"
if command -v corepack >/dev/null 2>&1; then
  corepack pnpm ui:build
else
  pnpm ui:build
fi

if [[ ! -f "$OPENCLAW_REPO/dist/entry.js" ]]; then
  log "Building OpenClaw"
  if command -v corepack >/dev/null 2>&1; then
    corepack pnpm build
  else
    pnpm build
  fi
fi

CONFIG_HOME="${OPENCLAW_HOME:-$HOME/.openclaw}"
CONFIG_PATH="$CONFIG_HOME/openclaw.json"

if [[ -f "$CONFIG_PATH" ]]; then
  log "Found existing OpenClaw config. Starting local gateway."
  pkill -f '^openclaw-gateway$' >/dev/null 2>&1 || true
  pkill -f 'openclaw.mjs gateway' >/dev/null 2>&1 || true
  pkill -f '^openclaw$' >/dev/null 2>&1 || true
  nohup node openclaw.mjs gateway --port 18789 > "$CONFIG_HOME/gateway-local.log" 2>&1 &
  sleep 4

  if ! curl -fsS http://127.0.0.1:18789/healthz >/dev/null 2>&1; then
    echo "Gateway health check failed. Last logs:" >&2
    tail -n 80 "$CONFIG_HOME/gateway-local.log" 2>/dev/null || true
    exit 1
  fi

  log "Gateway: http://127.0.0.1:18789"
  log "Health: ok"
else
  log "No OpenClaw config found at $CONFIG_PATH."
  log "Run: openclaw setup (or OC Setup in ReClaw), then OC Gateway Start."
fi

log "Fresh install complete"
