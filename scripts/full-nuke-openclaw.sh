#!/usr/bin/env bash
set -euo pipefail

# Full OpenClaw nuke workflow for macOS/Linux.
# Removes local OpenClaw service/state/CLI and (optionally) the repo — no backup.
#
# Usage:
#   ./scripts/full-nuke-openclaw.sh --yes [--remove-openclaw-repo]
#
# Optional:
#   --reclaw-repo /path/to/ReClaw
#   --openclaw-repo /path/to/openclaw
#   --remove-openclaw-repo

RECLAW_REPO=""
OPENCLAW_REPO=""
REMOVE_OPENCLAW_REPO="0"
ASSUME_YES="0"

if [[ -z "${RECLAW_REPO:-}" ]]; then
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  RECLAW_REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
fi

OPENCLAW_REPO_DEFAULT="$(cd "$RECLAW_REPO/.." && pwd)/openclaw"
OPENCLAW_REPO="$OPENCLAW_REPO_DEFAULT"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --reclaw-repo)
      RECLAW_REPO="$2"
      shift 2
      ;;
    --openclaw-repo)
      OPENCLAW_REPO="$2"
      shift 2
      ;;
    --remove-openclaw-repo)
      REMOVE_OPENCLAW_REPO="1"
      shift
      ;;
    --yes)
      ASSUME_YES="1"
      shift
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [[ ! -d "$RECLAW_REPO" ]]; then
  echo "ReClaw repo not found: $RECLAW_REPO" >&2
  exit 1
fi

if [[ "$ASSUME_YES" != "1" ]]; then
  cat <<MSG
This script will:
1) Remove OpenClaw service/state/CLI leftovers on this machine.
2) Optionally remove repo: $OPENCLAW_REPO (only if --remove-openclaw-repo)
MSG
  read -r -p "Continue? [y/N] " reply
  if [[ ! "$reply" =~ ^[Yy]$ ]]; then
    echo "Aborted."
    exit 0
  fi
fi

DOCKER_BIN="${DOCKER_BIN:-docker}"
if ! command -v "$DOCKER_BIN" >/dev/null 2>&1; then
  if [[ -x "/Applications/Docker.app/Contents/Resources/bin/docker" ]]; then
    DOCKER_BIN="/Applications/Docker.app/Contents/Resources/bin/docker"
  fi
fi

log() {
  echo "[nuke] $*"
}

log "Stopping OpenClaw managed service if available"
if command -v openclaw >/dev/null 2>&1; then
  openclaw gateway stop >/dev/null 2>&1 || true
  openclaw gateway uninstall >/dev/null 2>&1 || true
  openclaw uninstall --all --yes --non-interactive >/dev/null 2>&1 || true
fi

if [[ "$OSTYPE" == darwin* ]]; then
  log "Removing macOS launchd leftovers"
  launchctl bootout gui/$UID/ai.openclaw.gateway >/dev/null 2>&1 || true
  launchctl bootout gui/$UID/com.openclaw.gateway >/dev/null 2>&1 || true
  rm -f "$HOME/Library/LaunchAgents/ai.openclaw.gateway.plist"
  rm -f "$HOME/Library/LaunchAgents/com.openclaw.gateway.plist"
  rm -f "$HOME/Library/LaunchAgents/ai.openclaw."*.plist 2>/dev/null || true
  rm -f "$HOME/Library/LaunchAgents/com.openclaw."*.plist 2>/dev/null || true
  rm -rf "/Applications/OpenClaw.app"
fi

if [[ "$OSTYPE" == linux* ]]; then
  log "Removing Linux systemd user leftovers"
  systemctl --user disable --now openclaw-gateway.service >/dev/null 2>&1 || true
  rm -f "$HOME/.config/systemd/user/openclaw-gateway.service"
  rm -f "$HOME/.config/systemd/user/openclaw-gateway-"*.service 2>/dev/null || true
  systemctl --user daemon-reload >/dev/null 2>&1 || true
fi

log "Stopping local OpenClaw processes"
pkill -f '^openclaw-gateway$' >/dev/null 2>&1 || true
pkill -f 'openclaw.mjs gateway' >/dev/null 2>&1 || true
pkill -f 'npm exec openclaw gateway' >/dev/null 2>&1 || true
pkill -f 'pnpm openclaw gateway' >/dev/null 2>&1 || true
pkill -f '^openclaw$' >/dev/null 2>&1 || true

# Safety net: kill anything still listening on gateway port.
PORT_PIDS="$(lsof -tiTCP:18789 -sTCP:LISTEN 2>/dev/null || true)"
if [[ -n "$PORT_PIDS" ]]; then
  kill $PORT_PIDS >/dev/null 2>&1 || true
  sleep 1
  PORT_PIDS="$(lsof -tiTCP:18789 -sTCP:LISTEN 2>/dev/null || true)"
  if [[ -n "$PORT_PIDS" ]]; then
    kill -9 $PORT_PIDS >/dev/null 2>&1 || true
  fi
fi

if command -v "$DOCKER_BIN" >/dev/null 2>&1; then
  log "Cleaning Docker OpenClaw services/containers"
  if [[ -d "$OPENCLAW_REPO" && -f "$OPENCLAW_REPO/docker-compose.yml" ]]; then
    (
      cd "$OPENCLAW_REPO"
      if [[ -f .env ]]; then
        "$DOCKER_BIN" compose --env-file .env down --remove-orphans >/dev/null 2>&1 || true
      else
        "$DOCKER_BIN" compose down --remove-orphans >/dev/null 2>&1 || true
      fi
    )
  fi

  # Remove dangling OpenClaw containers and network leftovers by name.
  ids="$($DOCKER_BIN ps -aq --filter name=openclaw 2>/dev/null || true)"
  if [[ -n "$ids" ]]; then
    $DOCKER_BIN rm -f $ids >/dev/null 2>&1 || true
  fi

  nets="$($DOCKER_BIN network ls --format '{{.Name}}' 2>/dev/null | grep -E '^openclaw' || true)"
  if [[ -n "$nets" ]]; then
    while IFS= read -r n; do
      [[ -z "$n" ]] && continue
      $DOCKER_BIN network rm "$n" >/dev/null 2>&1 || true
    done <<< "$nets"
  fi
fi

log "Removing OpenClaw state/config leftovers"
rm -rf "$HOME/.openclaw"
rm -rf "$HOME/.openclaw-workspace"
rm -rf "$HOME/.openclaw"-* 2>/dev/null || true

# Clean global CLI installs (best effort)
npm rm -g openclaw >/dev/null 2>&1 || true
pnpm remove -g openclaw >/dev/null 2>&1 || true
bun remove -g openclaw >/dev/null 2>&1 || true

if [[ "$REMOVE_OPENCLAW_REPO" == "1" && -d "$OPENCLAW_REPO" ]]; then
  log "Removing OpenClaw git clone: $OPENCLAW_REPO"
  rm -rf "$OPENCLAW_REPO"
fi

log "Nuke complete"
