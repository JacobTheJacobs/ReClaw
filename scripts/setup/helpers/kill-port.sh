#!/usr/bin/env bash
set -euo pipefail

PORT=${1:-}
if [ -z "$PORT" ]; then
  echo "[helpers/kill-port] Usage: $0 <port> — this is a safe helper that tries to suggest or kill processes using the port"
  exit 2
fi

echo "[helpers/kill-port] Attempting to identify process on port $PORT"
if command -v lsof >/dev/null 2>&1; then
  pids=$(lsof -iTCP:$PORT -sTCP:LISTEN -t || true)
  if [ -n "$pids" ]; then
    echo "Found PIDs: $pids — sending TERM"
    for pid in $pids; do
      kill -15 "$pid" || true
    done
  else
    echo "No listening process found by lsof on port $PORT"
  fi
else
  echo "lsof not available; on Windows or minimal shells please use the PowerShell helper or OS tools"
fi

echo "Done (non-destructive)."
