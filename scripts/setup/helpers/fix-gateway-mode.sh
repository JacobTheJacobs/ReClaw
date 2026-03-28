#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR=$(cd "$(dirname "$0")/../.." && pwd)

echo "[helpers/fix-gateway-mode] Checking gateway mode (non-destructive)"
echo "If a known gateway mode fix is required, this script would apply a safe fix."
echo "Creating marker: .gateway-fix-suggested"
mkdir -p "$ROOT_DIR/scripts/setup/helpers/.markers" || true
echo "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$ROOT_DIR/scripts/setup/helpers/.markers/gateway-fix-suggested"
echo "Done (no destructive actions performed)."
