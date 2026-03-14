#!/usr/bin/env bash
set -euo pipefail

BACKUP_DIR="${BACKUP_DIR:-$HOME/Backups/openclaw}"
KEEP_DAYS="${KEEP_DAYS:-7}"
SKIP_SECURITY="0"
DRY_RUN="0"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --backup-dir)
      BACKUP_DIR="$2"
      shift 2
      ;;
    --keep-days)
      KEEP_DAYS="$2"
      shift 2
      ;;
    --skip-security)
      SKIP_SECURITY="1"
      shift
      ;;
    --dry-run)
      DRY_RUN="1"
      shift
      ;;
    --help|-h)
      cat <<'USAGE'
Usage: backup-openclaw-daily.sh [options]

Options:
  --backup-dir <path>   Backup directory (default: ~/Backups/openclaw)
  --keep-days <days>    Delete backups older than N days (default: 7)
  --skip-security       Skip openclaw security audit --json step
  --dry-run             Print commands and retention candidates without executing
  --help                Show this help
USAGE
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if ! [[ "$KEEP_DAYS" =~ ^[0-9]+$ ]]; then
  echo "--keep-days must be a non-negative integer." >&2
  exit 1
fi

DATE="$(date +%Y%m%d-%H%M%S)"
mkdir -p "$BACKUP_DIR"
LOG_FILE="$BACKUP_DIR/backup-$DATE.log"
ARCHIVE_PATH="$BACKUP_DIR/openclaw-$DATE.tar.gz"

log() {
  local message="$1"
  echo "[$(date +%Y-%m-%dT%H:%M:%S%z)] $message" | tee -a "$LOG_FILE"
}

run_cmd() {
  local step="$1"
  shift
  log "$step"
  if [[ "$DRY_RUN" == "1" ]]; then
    echo "[dry-run] $*" | tee -a "$LOG_FILE"
    return 0
  fi

  "$@" >> "$LOG_FILE" 2>&1
}

if ! command -v openclaw >/dev/null 2>&1; then
  if [[ "$DRY_RUN" == "1" ]]; then
    echo "[dry-run] openclaw command not found in PATH; showing planned commands only." | tee -a "$LOG_FILE"
  else
    echo "openclaw command not found in PATH." >&2
    exit 1
  fi
fi

log "=== OpenClaw daily backup started: $DATE ==="
run_cmd "Running health check" openclaw health --json
run_cmd "Creating verified backup" openclaw backup create --verify --output "$ARCHIVE_PATH"

if [[ "$SKIP_SECURITY" != "1" ]]; then
  run_cmd "Running security audit" openclaw security audit --json
fi

log "Applying retention cleanup (older than $KEEP_DAYS days)"
if [[ "$DRY_RUN" == "1" ]]; then
  find "$BACKUP_DIR" -maxdepth 1 -type f \
    \( -name 'openclaw-*.tar.gz' -o -name 'openclaw-*.tar.gz.enc' -o -name 'openclaw-*.zip' \) \
    -mtime "+$KEEP_DAYS" -print | sed 's/^/[dry-run] delete /' | tee -a "$LOG_FILE" || true
else
  find "$BACKUP_DIR" -maxdepth 1 -type f \
    \( -name 'openclaw-*.tar.gz' -o -name 'openclaw-*.tar.gz.enc' -o -name 'openclaw-*.zip' \) \
    -mtime "+$KEEP_DAYS" -print -delete >> "$LOG_FILE" 2>&1 || true
fi

log "=== OpenClaw daily backup complete ==="
log "Backup archive: $ARCHIVE_PATH"
log "Log file: $LOG_FILE"
