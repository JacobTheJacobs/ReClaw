#!/usr/bin/env bash
set -euo pipefail

echo "[helpers/pnpm-install] Attempting to enable corepack and prepare pnpm (non-destructive)"
if command -v corepack >/dev/null 2>&1; then
  corepack enable || true
  corepack prepare pnpm@latest --activate || true
  echo "corepack prepared pnpm (or was already active)"
else
  echo "corepack not available. If node >=16.10 is installed, corepack should be present."
  echo "Skipping automatic pnpm install to avoid destructive changes."
fi

echo "If pnpm is still missing, please install pnpm manually or enable corepack."
