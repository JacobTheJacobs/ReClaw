#!/usr/bin/env bash
set -euo pipefail

# Local macOS recovery flow (no Docker):
# clone -> install -> build (if needed) -> restore -> start gateway
#
# Usage:
#   ./scripts/recover-openclaw-local-mac.sh [--password "reclaw123"]
#
# Optional:
#   --workspace-root /Users/jacob/githuff
#   --archive /Users/jacob/githuff/claw-backup/backup.zip
#   --openclaw-repo /Users/jacob/githuff/openclaw

PASSWORD="${RECLAW_PASSWORD:-}"
BACKUP_DIR="${RECLAW_BACKUP_DIR:-${BACKUP_DIR:-}}"
WORKSPACE_ROOT=""
OPENCLAW_REPO=""
ARCHIVE_PATH=""

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RECLAW_REPO="$(cd "$SCRIPT_DIR/.." && pwd)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --password)
      PASSWORD="$2"
      shift 2
      ;;
    --workspace-root)
      WORKSPACE_ROOT="$2"
      shift 2
      ;;
    --openclaw-repo)
      OPENCLAW_REPO="$2"
      shift 2
      ;;
    --archive)
      ARCHIVE_PATH="$2"
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

if [[ -z "$BACKUP_DIR" ]]; then
  if [[ "$RECLAW_REPO" == *".app/Contents/Resources"* ]]; then
    BACKUP_DIR="$HOME/claw-backup"
  else
    BACKUP_DIR="$WORKSPACE_ROOT/claw-backup"
  fi
fi

if [[ -z "$ARCHIVE_PATH" ]]; then
  ARCHIVE_PATH="$BACKUP_DIR/backup.zip"
fi

log() {
  echo "[recover-local] $*"
}

if [[ ! -f "$ARCHIVE_PATH" ]]; then
  echo "Backup archive not found: $ARCHIVE_PATH" >&2
  exit 1
fi

log "Ensuring OpenClaw repo exists"
if [[ ! -d "$OPENCLAW_REPO/.git" ]]; then
  git clone https://github.com/openclaw/openclaw.git "$OPENCLAW_REPO"
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

log "Restoring backup with ReClaw"
cd "$RECLAW_REPO"
RESTORE_ARGS=(node bin/cli.js restore)
if [[ -n "$PASSWORD" ]]; then
  RESTORE_ARGS+=(--password "$PASSWORD")
fi
RESTORE_ARGS+=("$ARCHIVE_PATH")

if ! "${RESTORE_ARGS[@]}"; then
  log "Primary archive restore failed. Looking for latest valid fallback backup..."
  FALLBACK_ARCHIVE="$(node -e '
    const fs = require("fs");
    const path = require("path");
    const AdmZip = require("adm-zip");

    const repo = process.argv[1];
    const backupsDir = path.join(repo, "backups");
    if (!fs.existsSync(backupsDir)) process.exit(1);

    const files = fs.readdirSync(backupsDir)
      .filter((f) => /^openclaw_backup_.*\.zip$/.test(f))
      .map((f) => path.join(backupsDir, f))
      .filter((p) => fs.statSync(p).size >= 10 * 1024 * 1024)
      .sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs);

    const required = [
      (n) => n === "manifest.json",
      (n) => n === "openclaw.json" || n.endsWith("/openclaw.json"),
      (n) => n === "workspaces" || n === "workspaces/" || n.startsWith("workspaces/"),
      (n) => n === "plugins" || n === "plugins/" || n.startsWith("plugins/"),
      (n) => n === "credentials" || n === "credentials/" || n.startsWith("credentials/")
    ];

    for (const candidate of files) {
      try {
        const zip = new AdmZip(candidate);
        const names = zip.getEntries().map((e) => e.entryName.replace(/^\.\//, ""));
        const ok = required.every((pred) => names.some(pred));
        if (ok) {
          process.stdout.write(candidate);
          process.exit(0);
        }
      } catch (_) {
        // try next archive
      }
    }

    process.exit(1);
  ' "$RECLAW_REPO" 2>/dev/null || true)"

  if [[ -z "$FALLBACK_ARCHIVE" ]]; then
    echo "No valid fallback backup found in $RECLAW_REPO/backups" >&2
    exit 1
  fi

  log "Retrying restore with fallback archive: $FALLBACK_ARCHIVE"
  FALLBACK_ARGS=(node bin/cli.js restore)
  if [[ -n "$PASSWORD" ]]; then
    FALLBACK_ARGS+=(--password "$PASSWORD")
  fi
  FALLBACK_ARGS+=("$FALLBACK_ARCHIVE")
  "${FALLBACK_ARGS[@]}"
fi

log "Verifying restored payload"
node scripts/verify-openclaw-state.js restored

log "Forcing gateway.mode=local when missing"
node -e '
const fs = require("fs");
const path = require("path");
const home = process.env.OPENCLAW_HOME || path.join(process.env.HOME, ".openclaw");
const cfgPath = path.join(home, "openclaw.json");
if (!fs.existsSync(cfgPath)) process.exit(0);
const cfg = JSON.parse(fs.readFileSync(cfgPath, "utf8"));
cfg.gateway = cfg.gateway || {};
if (!cfg.gateway.mode) {
  cfg.gateway.mode = "local";
  fs.writeFileSync(cfgPath, JSON.stringify(cfg, null, 2));
  console.log("[recover-local] Set gateway.mode=local");
}
'

log "Running doctor fix"
cd "$OPENCLAW_REPO"
node openclaw.mjs doctor --fix --non-interactive || true

log "Starting local gateway"
pkill -f '^openclaw-gateway$' >/dev/null 2>&1 || true
pkill -f 'openclaw.mjs gateway' >/dev/null 2>&1 || true
pkill -f '^openclaw$' >/dev/null 2>&1 || true
nohup node openclaw.mjs gateway --port 18789 > "$HOME/.openclaw/gateway-local.log" 2>&1 &
sleep 4

if ! curl -fsS http://127.0.0.1:18789/healthz >/dev/null 2>&1; then
  echo "Gateway health check failed. Last logs:" >&2
  tail -n 80 "$HOME/.openclaw/gateway-local.log" 2>/dev/null || true
  exit 1
fi

log "Recovery complete"
log "Gateway: http://127.0.0.1:18789"
log "Health: ok"
