#!/usr/bin/env bash
set -euo pipefail

# End-to-end Docker recovery drill for OpenClaw + ReClaw restore.
# Usage:
#   ./scripts/docker-recovery-drill.sh \
#     --openclaw-repo /Users/jacob/githuff/openclaw \
#     --archive /Users/jacob/githuff/claw-backup/backup.zip \
#     --password 'reclaw123'

OPENCLAW_REPO=""
ARCHIVE_PATH=""
ARCHIVE_PASSWORD=""
DOCKER_BIN="${DOCKER_BIN:-docker}"

if ! command -v "$DOCKER_BIN" >/dev/null 2>&1; then
  if [[ -x "/Applications/Docker.app/Contents/Resources/bin/docker" ]]; then
    DOCKER_BIN="/Applications/Docker.app/Contents/Resources/bin/docker"
  fi
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    --openclaw-repo)
      OPENCLAW_REPO="$2"
      shift 2
      ;;
    --archive)
      ARCHIVE_PATH="$2"
      shift 2
      ;;
    --password)
      ARCHIVE_PASSWORD="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [[ -z "$OPENCLAW_REPO" || -z "$ARCHIVE_PATH" || -z "$ARCHIVE_PASSWORD" ]]; then
  echo "Missing required args." >&2
  echo "Required: --openclaw-repo --archive --password" >&2
  exit 1
fi

if [[ ! -d "$OPENCLAW_REPO" ]]; then
  echo "OpenClaw repo not found: $OPENCLAW_REPO" >&2
  exit 1
fi

if [[ ! -f "$ARCHIVE_PATH" ]]; then
  echo "Backup archive not found: $ARCHIVE_PATH" >&2
  exit 1
fi

command -v "$DOCKER_BIN" >/dev/null 2>&1 || {
  echo "Docker binary not found: $DOCKER_BIN" >&2
  exit 1
}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RECLAW_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OPENCLAW_HOME="${OPENCLAW_HOME:-$HOME/.openclaw}"
OPENCLAW_WORKSPACE="${OPENCLAW_WORKSPACE:-$OPENCLAW_HOME/workspace}"
TOKEN="$(openssl rand -hex 24)"

echo "[drill] Resetting local OpenClaw state at $OPENCLAW_HOME"
mkdir -p "$OPENCLAW_HOME"
# Some files can be root-owned from previous container runs; clear them via a container.
"$DOCKER_BIN" run --rm -v "$OPENCLAW_HOME:/target" alpine:3.20 sh -c 'rm -rf /target/* /target/.[!.]* /target/..?* 2>/dev/null || true' >/dev/null 2>&1 || true
mkdir -p "$OPENCLAW_HOME" "$OPENCLAW_WORKSPACE"

cd "$OPENCLAW_REPO"
cat > .env <<EOF
OPENCLAW_IMAGE=ghcr.io/openclaw/openclaw:latest
OPENCLAW_CONFIG_DIR=$OPENCLAW_HOME
OPENCLAW_WORKSPACE_DIR=$OPENCLAW_WORKSPACE
OPENCLAW_GATEWAY_BIND=loopback
OPENCLAW_GATEWAY_PORT=18789
OPENCLAW_BRIDGE_PORT=18790
OPENCLAW_GATEWAY_TOKEN=$TOKEN
EOF

echo "[drill] Pulling OpenClaw Docker image"
"$DOCKER_BIN" pull ghcr.io/openclaw/openclaw:latest >/dev/null

echo "[drill] Starting OpenClaw gateway container"
"$DOCKER_BIN" compose --env-file .env up -d openclaw-gateway

echo "[drill] Running ReClaw restore into host-mounted OpenClaw home"
cd "$RECLAW_DIR"
OPENCLAW_DOCKER_HOME=/home/node/.openclaw npm run restore -- --password "$ARCHIVE_PASSWORD" "$ARCHIVE_PATH"

echo "[drill] Restarting gateway to load restored config"
cd "$OPENCLAW_REPO"
"$DOCKER_BIN" compose --env-file .env restart openclaw-gateway >/dev/null
sleep 5

echo "[drill] Health check"
curl -fsS http://127.0.0.1:18789/healthz | cat

echo "[drill] Verifying restored paths"
node -e '
const fs = require("fs");
const path = require("path");
const base = process.env.OPENCLAW_HOME || path.join(process.env.HOME, ".openclaw");
const required = [".env", "openclaw.json", "logs", "workspaces", "plugins", "credentials"];
let missing = 0;
for (const item of required) {
  const full = path.join(base, item);
  const ok = fs.existsSync(full);
  console.log(`${item}:${ok ? "present" : "missing"}`);
  if (!ok) missing += 1;
}
if (missing > 0) process.exit(2);
' 

echo "[drill] Docker container status"
"$DOCKER_BIN" inspect --format '{{.State.Status}} {{.RestartCount}} {{.State.Health.Status}}' openclaw-openclaw-gateway-1 | cat

echo "[drill] Complete"
