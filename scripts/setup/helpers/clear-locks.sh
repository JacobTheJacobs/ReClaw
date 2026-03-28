#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR=$(cd "$(dirname "$0")/../.." && pwd)

echo "[helpers/clear-locks] Looking for install.lock files to clear (non-destructive)"
for f in "$ROOT_DIR"/install.lock "$ROOT_DIR"/openclaw/install.lock; do
  if [ -f "$f" ]; then
    echo "Found lock: $f — renaming to ${f}.bak"
    mv "$f" "${f}.bak"
  else
    echo "No lock at $f"
  fi
done

echo "Done. No config files removed; locks were renamed if present."
