#!/usr/bin/env bash
set -euo pipefail

# Full OpenClaw reset workflow for macOS/Linux.
# 1) Create backup.zip with ReClaw
# 2) Remove OpenClaw service + state + Docker leftovers
#
# Usage:
#   ./scripts/full-reset-openclaw.sh --yes [--password "reclaw123"]
#
# Optional:
#   --reclaw-repo /path/to/ReClaw
#   --openclaw-repo /path/to/openclaw
#   --backup-dir /path/to/claw-backup
#   --backup-name backup.zip
#   --remove-openclaw-repo

RECLAW_REPO=""
OPENCLAW_REPO=""
BACKUP_DIR="${BACKUP_DIR:-}"
BACKUP_NAME="backup.zip"
PASSWORD="${RECLAW_PASSWORD:-}"
REMOVE_OPENCLAW_REPO="0"
ASSUME_YES="0"
ALLOW_SMALL_BACKUP="0"

if [[ -z "${RECLAW_REPO:-}" ]]; then
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  RECLAW_REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
fi

OPENCLAW_REPO_DEFAULT="$(cd "$RECLAW_REPO/.." && pwd)/openclaw"
OPENCLAW_REPO="$OPENCLAW_REPO_DEFAULT"
if [[ -z "$BACKUP_DIR" ]]; then
  if [[ "$RECLAW_REPO" == *".app/Contents/Resources"* ]]; then
    BACKUP_DIR="$HOME/claw-backup"
  else
    BACKUP_DIR="$(cd "$RECLAW_REPO/.." && pwd)/claw-backup"
  fi
fi

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
    --backup-dir)
      BACKUP_DIR="$2"
      shift 2
      ;;
    --backup-name)
      BACKUP_NAME="$2"
      shift 2
      ;;
    --password)
      PASSWORD="$2"
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
    --allow-small-backup)
      ALLOW_SMALL_BACKUP="1"
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

mkdir -p "$BACKUP_DIR"

if [[ "$ASSUME_YES" != "1" ]]; then
  cat <<MSG
This script will:
1) Create backup archive at: $BACKUP_DIR/$BACKUP_NAME
2) Remove OpenClaw service/state/docker leftovers on this machine.
3) Optionally remove repo: $OPENCLAW_REPO (only if --remove-openclaw-repo)
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
  echo "[reset] $*"
}

log "Creating backup with ReClaw"
cd "$RECLAW_REPO"
export BACKUP_DIR="$BACKUP_DIR"
backup_args=(bin/cli.js backup)
if [[ -n "$PASSWORD" ]]; then
  backup_args+=(--password "$PASSWORD")
else
  log "No backup password provided; creating unencrypted backup"
fi
node "${backup_args[@]}"

shopt -s nullglob
backup_candidates=(
  "$RECLAW_REPO/backups"/openclaw_backup_*.zip
  "$RECLAW_REPO/backups"/openclaw_backup_*.tar.gz
  "$RECLAW_REPO/backups"/openclaw_backup_*.tar.gz.enc
)
shopt -u nullglob
if [[ ${#backup_candidates[@]} -eq 0 ]]; then
  echo "No backup archive generated under $RECLAW_REPO/backups" >&2
  exit 1
fi
LATEST_BACKUP="$(ls -1t "${backup_candidates[@]}" 2>/dev/null | head -n1 || true)"
if [[ -z "$LATEST_BACKUP" ]]; then
  echo "No backup archive generated under $RECLAW_REPO/backups" >&2
  exit 1
fi

# Guardrail: prevent overwriting backup.zip with tiny/incomplete snapshots.
BACKUP_SIZE_BYTES="$(wc -c < "$LATEST_BACKUP" | tr -d ' ')"
if [[ "$ALLOW_SMALL_BACKUP" != "1" ]]; then
  if [[ "$BACKUP_SIZE_BYTES" -lt 10485760 ]]; then
    echo "Refusing to continue: generated backup is too small (${BACKUP_SIZE_BYTES} bytes)." >&2
    echo "Your OpenClaw state may already be empty. Use a previous full backup instead." >&2
    echo "If this is intentional, rerun with --allow-small-backup." >&2
    exit 1
  fi

  archive_lower="$(printf '%s' "$LATEST_BACKUP" | tr '[:upper:]' '[:lower:]')"
  if [[ "$archive_lower" == *.zip ]]; then
    if ! node -e '
      const AdmZip = require("adm-zip");
      const zipPath = process.argv[1];
      const zip = new AdmZip(zipPath);
      const names = zip.getEntries().map((e) => e.entryName.replace(/^\.\//, ""));
      const has = (pred) => names.some(pred);
      const required = [
        ["manifest.json", (n) => n === "manifest.json"],
        ["openclaw.json", (n) => n === "openclaw.json" || n.endsWith("/openclaw.json")],
        ["workspaces", (n) => n === "workspaces" || n === "workspaces/" || n.startsWith("workspaces/")],
        ["plugins", (n) => n === "plugins" || n === "plugins/" || n.startsWith("plugins/")],
        ["credentials", (n) => n === "credentials" || n === "credentials/" || n.startsWith("credentials/")]
      ];

      const missing = required
        .filter(([, predicate]) => !has(predicate))
        .map(([label]) => label);

      if (missing.length > 0) {
        console.error(missing.join(","));
        process.exit(1);
      }
    ' "$LATEST_BACKUP" 2>/dev/null; then
      MISSING_ITEMS="$(node -e '
        const AdmZip = require("adm-zip");
        const zipPath = process.argv[1];
        const zip = new AdmZip(zipPath);
        const names = zip.getEntries().map((e) => e.entryName.replace(/^\.\//, ""));
        const has = (pred) => names.some(pred);
        const required = [
          ["manifest.json", (n) => n === "manifest.json"],
          ["openclaw.json", (n) => n === "openclaw.json" || n.endsWith("/openclaw.json")],
          ["workspaces", (n) => n === "workspaces" || n === "workspaces/" || n.startsWith("workspaces/")],
          ["plugins", (n) => n === "plugins" || n === "plugins/" || n.startsWith("plugins/")],
          ["credentials", (n) => n === "credentials" || n === "credentials/" || n.startsWith("credentials/")]
        ];
        const missing = required
          .filter(([, predicate]) => !has(predicate))
          .map(([label]) => label);
        process.stdout.write(missing.join(", "));
      ' "$LATEST_BACKUP" 2>/dev/null || true)"
      echo "Refusing to continue: backup appears incomplete (missing ${MISSING_ITEMS:-required entries})." >&2
      echo "Use a previous full backup from $RECLAW_REPO/backups." >&2
      echo "If this is intentional, rerun with --allow-small-backup." >&2
      exit 1
    fi
  elif [[ "$archive_lower" == *.tar.gz ]]; then
    if ! tar -tzf "$LATEST_BACKUP" >/tmp/reclaw_backup_entries.txt 2>/dev/null; then
      echo "Refusing to continue: could not read tar.gz backup entries." >&2
      echo "If this is intentional, rerun with --allow-small-backup." >&2
      exit 1
    fi
    missing=()
    grep -qE '^manifest\.json$' /tmp/reclaw_backup_entries.txt || missing+=('manifest.json')
    grep -qE '(^|.*/)openclaw\.json$' /tmp/reclaw_backup_entries.txt || missing+=('openclaw.json')
    grep -qE '(^|.*/)workspaces(/|$)' /tmp/reclaw_backup_entries.txt || missing+=('workspaces')
    grep -qE '(^|.*/)plugins(/|$)' /tmp/reclaw_backup_entries.txt || missing+=('plugins')
    grep -qE '(^|.*/)credentials(/|$)' /tmp/reclaw_backup_entries.txt || missing+=('credentials')
    rm -f /tmp/reclaw_backup_entries.txt || true
    if [[ ${#missing[@]} -gt 0 ]]; then
      echo "Refusing to continue: backup appears incomplete (missing ${missing[*]})." >&2
      echo "Use a previous full backup from $RECLAW_REPO/backups." >&2
      echo "If this is intentional, rerun with --allow-small-backup." >&2
      exit 1
    fi
  else
    log "Skipping archive content verification for encrypted archive."
  fi
fi

cp -f "$LATEST_BACKUP" "$BACKUP_DIR/$BACKUP_NAME"
log "Backup saved to: $BACKUP_DIR/$BACKUP_NAME"

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

log "Reset complete"
log "Backup archive: $BACKUP_DIR/$BACKUP_NAME"
