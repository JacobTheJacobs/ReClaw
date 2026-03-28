---
name: reclaw
description: >
  Manage OpenClaw: backup, restore, repair, recover, schedule, setup.
  Use when gateway is offline, config is broken, backups are needed,
  or OpenClaw needs a fresh install. Diagnoses and fixes automatically.
argument-hint: [action] (backup, restore, repair, recover, doctor, setup, status, schedule)
---

# ReClaw — OpenClaw Management

Diagnose → pick action → run → parse output → verify fix.

## Setup

```bash
dotnet --version   # 10.0+
git --version
cd "$(git rev-parse --show-toplevel)" && dotnet build ReClaw.slnx -c Release --verbosity quiet
CLI="dotnet run --project $(git rev-parse --show-toplevel)/src/ReClaw.Cli/ReClaw.Cli.csproj --"
```

## CRITICAL: Space-separated subcommands

```bash
$CLI gateway status        # NOT: gateway-status
$CLI backup create         # NOT: backup-create
$CLI create                # root alias
$CLI restore -s <path>     # NOT: restore-archive
$CLI reset                 # NOT: nuke
```

## Decision Tree

```
User says "$ARGUMENTS"

1. Prerequisites ok? (dotnet 10+, git)
   NO → tell user to install

2. OpenClaw installed? (command -v openclaw || where openclaw)
   NO → $CLI openclaw rebuild --confirm-destructive

3. ALWAYS check gateway first: $CLI gateway status
   "Runtime: running" + "RPC probe: ok" → healthy
   "Runtime: stopped" / "RPC probe: failed" → broken
   Exit 0 → healthy, non-0 → broken

4. Gateway broken?
   → $CLI create (backup first!)
   → $CLI gateway repair (9-step auto-escalating)
   → $CLI gateway status (verify)

5. Config corrupt / data lost?
   → $CLI create → $CLI recover → $CLI gateway status

6. User needs:
   Backup?    → $CLI create [-p pass]
   Restore?   → $CLI restore -s <path> --preview then without --preview
   Schedule?  → $CLI backup schedule create [--kind daily|weekly|cron]
   Status?    → $CLI status + $CLI gateway status
   Unknown?   → $CLI doctor → interpret → suggest
```

## Commands

### Root
```bash
$CLI create [-p pass]                         # Backup
$CLI verify -s <archive>                      # Verify backup
$CLI restore -s <archive> [--preview] [-p]    # Restore
$CLI doctor [--repair] [--deep] [--force]     # Health check
$CLI fix [--force]                            # Deep fix with snapshot
$CLI recover [--no-doctor] [--no-fix]         # Escalating pipeline
$CLI rollback -s <archive> --confirm-rollback # Rollback
$CLI reset --preview  /  --confirm            # Reset (DESTRUCTIVE)
$CLI reset --reset-mode full --confirm        # Full wipe
$CLI status                                   # Paths + inventory
$CLI dashboard                                # Open web UI
$CLI cleanup-related [--apply --confirm]      # Clean artifacts
$CLI diagnostics export                       # Debug bundle
$CLI version                                  # Version
$CLI action-list                              # All 91 actions
$CLI action-schema --id <action-id>           # JSON schema
```

### Backup
```bash
$CLI backup create [--scope config] [-p pass] # Scoped/encrypted backup
$CLI backup verify -s <path>                  # Integrity check
$CLI backup list                              # List backups
$CLI backup restore -s <path> [--preview]     # Restore
$CLI backup diff --left <a> --right <b>       # Compare
$CLI backup export [--scope config+creds+sessions] # Scoped export
$CLI backup prune [--apply] [--keep-last 5]   # Prune (dry-run default)
$CLI backup schedule create                   # Daily (default)
$CLI backup schedule create --kind weekly --day-of-week Mon --at 03:00
$CLI backup schedule create --kind cron --expression "0 2 * * *"
$CLI backup schedule list                     # List schedules
$CLI backup schedule remove                   # Remove schedule
```

### Gateway
```bash
$CLI gateway status                           # Health (RUN FIRST)
$CLI gateway start                            # Start
$CLI gateway stop                             # Stop (destructive)
$CLI gateway repair                           # 9-step repair ladder
$CLI gateway logs                             # Tail logs (Ctrl+C)
$CLI gateway url                              # Dashboard URL + token
$CLI gateway token show [--reveal]            # Show token
$CLI gateway token generate                   # New token
$CLI gateway browser-diagnostics              # Browser diagnostics
```

### OpenClaw
```bash
$CLI openclaw rebuild --confirm-destructive   # Rebuild (DESTRUCTIVE)
$CLI openclaw cleanup-related [--apply --confirm] # Clean artifacts
```

## Key Options

| Option | Purpose |
|--------|---------|
| `--preview` | Show impact without applying (restore, reset, rollback, prune) |
| `--confirm` | Confirm destructive reset |
| `--confirm-destructive` | Confirm rebuild |
| `--confirm-rollback` | Confirm rollback |
| `--json` | JSON output |
| `-p <password>` | Encrypt/decrypt backup |
| `--reset-mode <mode>` | preserve-backups (default), preserve-config, preserve-cli, full |
| `--scope <scope>` | config, config+creds+sessions, full |
| `--keep-last <N>` | Prune: keep last N (default: 5) |
| `--older-than <dur>` | Prune: older than duration (default: 30d) |

## Output Parsing

### gateway status
```
"Runtime: running" + "RPC probe: ok"  → HEALTHY
"Runtime: stopped"                    → gateway start
"RPC probe: failed"                   → gateway repair
"Service: not registered"             → gateway repair
```

### doctor
```
Exit 0, no "FAIL"  → all good
"FAIL" / "error"   → run fix
"config missing"   → run recover
```

### create
```
Success → prints backup path: ~/claw-backup/openclaw_backup_*.tar.gz
Failure → check disk space, RECLAW_BACKUP_DIR
```

## Gateway Repair Ladder (9 steps)

`$CLI gateway repair` auto-escalates:
1. Mode fix (set gateway.mode local)
2. Doctor (openclaw doctor)
3. Session cleanup
4. Gateway reinstall
5. Start + poll (60s)
6. Stability check (10s)
7. Lock cleanup (stale .lock files)
8. Process kill (port 18789)
9. Detached fallback launch

## Recovery Pipeline

`$CLI recover` escalates: restore → doctor → fix → gateway repair

## Safety Rules

1. **Always** `gateway status` before and after any fix
2. **Always** `create` before destructive ops
3. **Always** `--preview` before restore or reset
4. **Escalate**: doctor → fix → gateway repair → recover → reset
5. **Ask user** before reset or rebuild
6. **Verify**: `gateway status` + `doctor` after every fix

## No Argument Given

If user types `/reclaw` with no argument:
```bash
$CLI gateway status
$CLI doctor
```
Parse output, report state, suggest next steps.

## Environment

| Variable | Default |
|----------|---------|
| `OPENCLAW_HOME` | `~/.openclaw` |
| `RECLAW_BACKUP_DIR` | `~/claw-backup` |
| `RECLAW_SETUP_FORCE` | `0` |
| `RECLAW_DEBUG` | `0` |

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Gateway offline | `create` → `gateway repair` → `gateway status` |
| Gateway stuck | Kill process → `gateway start` |
| Config corrupt | `create` → `recover` → `gateway status` |
| Fresh start | `create` → `reset --preview` → `reset --confirm` |
| Token missing | `gateway token show` or `gateway token generate` |
| Unknown | `doctor` → `diagnostics export` |
