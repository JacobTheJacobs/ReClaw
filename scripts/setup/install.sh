#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "$0")/../.." && pwd)
cd "$ROOT_DIR"

OPENCLAW_INSTALL_TIMEOUT=${OPENCLAW_INSTALL_TIMEOUT:-300}
OPENCLAW_RETRY=${OPENCLAW_RETRY:-3}
ASSUME_OFFLINE=${OPENCLAW_ASSUME_OFFLINE:-0}

log(){ echo "[install] $*"; }
err(){ echo "[install][ERROR] $*" >&2; }

run_or_stub(){
  if [ "$ASSUME_OFFLINE" = "1" ]; then
    log "MOCK MODE: would run: $*"
  else
    log "running: $*"
    eval "$@"
  fi
}

ensure_corepack_and_pnpm(){
  log "Ensuring corepack and pnpm available"
  if command -v corepack >/dev/null 2>&1; then
    run_or_stub corepack enable || true
    run_or_stub corepack prepare pnpm@latest --activate || true
  else
    err "corepack not available; trying to continue (node >=16.10 recommended)"
  fi
  if ! command -v pnpm >/dev/null 2>&1; then
    log "pnpm not found after corepack prepare; attempting helper"
    bash "${ROOT_DIR}/scripts/setup/helpers/pnpm-install.sh"
  fi
}

main(){
  log "Starting unattended setup flow"
  ensure_corepack_and_pnpm

  log "Running pnpm install"
  run_or_stub pnpm install --shamefully-hoist || true

  log "Installing gateway"
  run_or_stub pnpm openclaw gateway install || true

  log "Starting gateway"
  run_or_stub pnpm openclaw gateway start || true

  # Run onboarding with retries
  tries=0
  until [ $tries -ge $OPENCLAW_RETRY ]
  do
    tries=$((tries+1))
    log "Onboarding attempt $tries/$OPENCLAW_RETRY"
    if [ "$ASSUME_OFFLINE" = "1" ]; then
      log "MOCK MODE: skipping real onboarding"
      success=0
    else
      timeout "${OPENCLAW_INSTALL_TIMEOUT}s" pnpm openclaw installer/onboard/run -- --accept-license --auto && success=0 || success=1
    fi
    if [ "$success" = 0 ]; then
      log "Onboarding appears successful"
      mkdir -p openclaw
      if command -v date >/dev/null 2>&1; then
        date -u +%Y-%m-%dT%H:%M:%SZ > openclaw/.onboarded
      else
        echo "$(python -c 'import datetime; print(datetime.datetime.utcnow().isoformat()+"Z")')" > openclaw/.onboarded
      fi
      return 0
    else
      err "Onboard failed on attempt $tries"
      bash "${ROOT_DIR}/scripts/setup/helpers/fix-gateway-mode.sh" || true
      bash "${ROOT_DIR}/scripts/setup/helpers/clear-locks.sh" || true
      sleep 2
    fi
  done

  err "All onboarding attempts failed"
  return 2
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  main "$@"
fi
