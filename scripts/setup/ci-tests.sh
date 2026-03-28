#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "$0")/../.." && pwd)
cd "$ROOT_DIR"

echo "[ci-tests] Running install.sh in MOCK mode"
OPENCLAW_ASSUME_OFFLINE=1 OPENCLAW_INSTALL_TIMEOUT=5 bash "${ROOT_DIR}/scripts/setup/install.sh"

echo "[ci-tests] Running healthcheck in mock mode"
node "${ROOT_DIR}/scripts/setup/healthcheck.js" --mock

echo "[ci-tests] CI tests completed (mock run)."
