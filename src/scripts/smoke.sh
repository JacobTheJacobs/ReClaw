#!/usr/bin/env bash
set -euo pipefail

RID="${1:-linux-x64}"
DESKTOP_FLAG="${2:-}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_DIR="$ROOT/dist/cli/$RID"
DESKTOP_DIR="$ROOT/dist/desktop/$RID"
CLI_BIN="$CLI_DIR/ReClaw.Cli"
DESKTOP_BIN="$DESKTOP_DIR/ReClaw.Desktop"

if [[ -f "$CLI_BIN" ]]; then
  chmod +x "$CLI_BIN" || true
fi
if [[ -f "$DESKTOP_BIN" ]]; then
  chmod +x "$DESKTOP_BIN" || true
fi

if [[ ! -f "$CLI_BIN" ]]; then
  echo "CLI binary not found at $CLI_BIN. Run publish first."
  exit 1
fi

"$CLI_BIN" --help >/dev/null
"$CLI_BIN" version >/dev/null
"$CLI_BIN" status >/dev/null
"$CLI_BIN" action-list >/dev/null
"$CLI_BIN" backup verify --help >/dev/null
"$CLI_BIN" restore --help >/dev/null
"$CLI_BIN" doctor --help >/dev/null
"$CLI_BIN" fix --help >/dev/null
"$CLI_BIN" recover --help >/dev/null
"$CLI_BIN" rollback --help >/dev/null
"$CLI_BIN" reset --help >/dev/null

SMOKE_ROOT="$(mktemp -d)"
SRC_DIR="$SMOKE_ROOT/src"
DEST_DIR="$SMOKE_ROOT/dest"
ARCHIVE="$SMOKE_ROOT/sample.tar.gz"
mkdir -p "$SRC_DIR"
echo "smoke" > "$SRC_DIR/sample.txt"

"$CLI_BIN" backup create --source "$SRC_DIR" --out "$ARCHIVE" >/dev/null
"$CLI_BIN" restore --preview --snapshot "$ARCHIVE" --dest "$DEST_DIR" >/dev/null
"$CLI_BIN" rollback --preview --snapshot "$ARCHIVE" --dest "$DEST_DIR" >/dev/null
"$CLI_BIN" reset --preview --reset-mode preserve-backups >/dev/null
if "$CLI_BIN" reset --reset-mode preserve-backups >/dev/null; then
  echo "reset confirmation gate did not block"
  exit 1
fi

if [[ "$DESKTOP_FLAG" == "--desktop" && -f "$DESKTOP_BIN" ]]; then
  "$DESKTOP_BIN" >/dev/null 2>&1 &
  PID=$!
  sleep 3
  kill "$PID" || true
fi
