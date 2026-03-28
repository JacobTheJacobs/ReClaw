---
name: reclaw-openclaw-manager
description: >
  Universal OpenClaw management skill. Paste into any AI coding agent
  (Claude Code, Copilot, Codex, Windsurf, Cursor, VS Code, JetBrains)
  to install, backup, restore, repair, and maintain OpenClaw automatically.
---

# ReClaw — OpenClaw Management Skill

Manage OpenClaw: backup, restore, repair, monitor, schedule, setup.

## Setup

```bash
# Prerequisites
dotnet --version   # .NET SDK 10.0+
git --version

# Build (once)
git clone https://github.com/JacobTheJacobs/ReClaw.git /tmp/reclaw
cd /tmp/reclaw && dotnet build ReClaw.slnx -c Release

# CLI shorthand — use $CLI for all commands below
CLI="dotnet run --project /tmp/reclaw/src/ReClaw.Cli/ReClaw.Cli.csproj --"
# Windows: replace /tmp/reclaw with %TEMP%\reclaw
```

## CRITICAL: Command Format

Space-separated subcommands. Never hyphens.

```bash
$CLI gateway status        # NOT: gateway-status
$CLI backup create         # NOT: backup-create
$CLI create                # NOT: backup-create (root alias)
$CLI restore -s <path>     # NOT: restore-archive
$CLI reset                 # NOT: nuke
```

## Decision Tree

```
1. Prerequisites ok?
   NO  → install .NET 10 + Git

2. OpenClaw installed?  (command -v openclaw || where openclaw)
   NO  → $CLI openclaw rebuild --confirm-destructive

3. Gateway healthy?  ($CLI gateway status)
   Healthy = exit 0 + "Runtime: running" + "RPC probe: ok"
   Broken  = exit non-0 or "stopped" or "failed"

4. Gateway broken?
   → $CLI create                    # backup first
   → $CLI gateway repair            # auto-escalating 9-step ladder
   → $CLI gateway status            # verify

5. Config corrupt / data lost?
   → $CLI create                    # backup first
   → $CLI recover                   # escalating: restore → doctor → fix → gateway
   → $CLI gateway status            # verify

6. Escalation order (if previous step fails):
   doctor → fix → gateway repair → recover → reset --confirm
```

## CLI Commands

### Root Commands
```bash
$CLI create                              # Backup current state
$CLI create -p <password>                # Encrypted backup
$CLI verify -s <archive>                 # Verify backup integrity
$CLI restore -s <archive> --preview      # Preview restore impact
$CLI restore -s <archive>               # Restore from backup
$CLI doctor                              # Health check
$CLI doctor --repair --force             # Health check + force repair
$CLI doctor --deep                       # Deep health check
$CLI doctor --generate-token             # Generate gateway token
$CLI fix                                 # Deep fix with snapshot
$CLI fix --force                         # Force deep fix
$CLI recover                             # Full recovery pipeline
$CLI recover --no-doctor --no-fix        # Skip steps in pipeline
$CLI rollback -s <archive> --preview     # Preview rollback
$CLI rollback -s <archive> --confirm-rollback  # Apply rollback
$CLI reset --preview                     # Preview reset
$CLI reset --confirm                     # Apply reset (DESTRUCTIVE)
$CLI reset --reset-mode full --confirm   # Full wipe (DESTRUCTIVE)
$CLI status                              # Paths + backup inventory
$CLI dashboard                           # Open web dashboard
$CLI cleanup-related                     # List OpenClaw artifacts
$CLI cleanup-related --apply --confirm   # Remove artifacts (DESTRUCTIVE)
$CLI diagnostics export                  # Export debug bundle
$CLI version                             # CLI version
$CLI action-list                         # List all 91 internal actions
$CLI action-schema --id <action-id>      # Export action JSON schema
```

### Backup Commands
```bash
$CLI backup create                       # Create backup
$CLI backup create -p <password>         # Encrypted backup
$CLI backup create --scope config        # Config-only backup
$CLI backup verify -s <archive>          # Verify integrity
$CLI backup list                         # List backups
$CLI backup restore -s <archive> --preview  # Preview restore
$CLI backup restore -s <archive>         # Restore from backup
$CLI backup diff --left <a> --right <b>  # Compare two backups
$CLI backup export                       # Export scoped backup
$CLI backup export --scope config+creds+sessions  # Scoped export
$CLI backup prune                        # Dry-run prune plan
$CLI backup prune --apply                # Apply prune (DESTRUCTIVE)
$CLI backup prune --keep-last 3          # Keep last 3 (default: 5)
$CLI backup prune --older-than 14d       # Older than 14 days (default: 30d)
$CLI backup schedule create              # Daily schedule (default)
$CLI backup schedule create --kind weekly --day-of-week Mon --at 03:00
$CLI backup schedule create --kind cron --expression "0 2 * * *"
$CLI backup schedule list                # List schedules
$CLI backup schedule remove              # Remove schedule (DESTRUCTIVE)
```

### Gateway Commands
```bash
$CLI gateway status                      # Check health (ALWAYS RUN FIRST)
$CLI gateway start                       # Start gateway
$CLI gateway stop                        # Stop gateway (DESTRUCTIVE)
$CLI gateway repair                      # 9-step auto-escalating repair
$CLI gateway logs                        # Tail logs (Ctrl+C to stop)
$CLI gateway url                         # Dashboard URL + token
$CLI gateway token show                  # Show token (masked)
$CLI gateway token show --reveal         # Show full token
$CLI gateway token generate              # Generate new token
$CLI gateway browser-diagnostics         # Browser access diagnostics
```

### OpenClaw Commands
```bash
$CLI openclaw rebuild --confirm-destructive  # Rebuild OpenClaw (DESTRUCTIVE)
$CLI openclaw rebuild --clean-install --confirm-destructive  # Clean rebuild
$CLI openclaw cleanup-related            # List artifacts
$CLI openclaw cleanup-related --apply --confirm  # Remove artifacts
```

## Key Options

| Option | Commands | Purpose |
|--------|----------|---------|
| `--preview` | restore, reset, rollback, rebuild, prune | Show impact without applying |
| `--confirm` | reset, cleanup-related | Confirm destructive action |
| `--confirm-destructive` | openclaw rebuild | Confirm rebuild |
| `--confirm-rollback` | rollback | Confirm rollback |
| `--json` | Most commands | JSON output for parsing |
| `-p <password>` | create, restore, recover, fix, export | Encrypt/decrypt |
| `--repair` | doctor | Auto-repair found issues |
| `--force` | doctor, fix | Force repair even if checks pass |
| `--deep` | doctor | Deep health analysis |
| `--reset-mode <mode>` | reset, restore, recover | See reset modes below |
| `--scope <scope>` | backup create, export | config, config+creds+sessions, full |

### Reset Modes
| Mode | Keeps |
|------|-------|
| `preserve-backups` | Backups (default) |
| `preserve-config` | Config + backups |
| `preserve-cli` | CLI + config + backups |
| `full` | Nothing — complete wipe |

## Output Parsing

### gateway status
```
"Runtime: running" + "RPC probe: ok"  → HEALTHY
"Runtime: stopped"                    → run: gateway start
"RPC probe: failed"                   → run: gateway repair
"Service: not registered"             → run: gateway repair
Exit code 0                           → healthy
Exit code non-0                       → broken
```

### doctor
```
Exit 0, no "FAIL"/"error"  → all good
"FAIL" or "error"          → run: fix
"config missing"           → run: recover
```

### create
```
Prints backup path on success: ~/claw-backup/openclaw_backup_*.tar.gz
Failure → check disk space, check RECLAW_BACKUP_DIR exists
```

## Gateway Repair Ladder (9 steps)

`$CLI gateway repair` auto-escalates through these steps:

```
1. Mode fix       → set gateway.mode to "local" if unset
2. Doctor         → openclaw doctor --non-interactive --yes
3. Session clean  → clear stale session store
4. Reinstall      → uninstall + reinstall gateway service
5. Start + poll   → start gateway, poll 60s for healthy
6. Stability      → 10s stability check
7. Lock cleanup   → remove stale gateway*.lock files
8. Process kill   → kill processes on port 18789
9. Detached       → fallback: launch gateway as detached process
```

## Recovery Pipeline

`$CLI recover` runs escalating steps:

```
1. Restore  → apply most recent backup
2. Doctor   → health checks + quick fixes
3. Fix      → deeper repair with snapshot
4. Gateway  → restart > reinstall > rebuild
```

Skip options: `--no-reset`, `--no-doctor`, `--no-fix`

## Safety Rules

1. **Always** `gateway status` before and after any fix
2. **Always** `create` before destructive ops (reset, recover, rebuild)
3. **Always** `--preview` before restore or reset
4. **Escalate** gradually: doctor → fix → gateway repair → recover → reset
5. **Ask user** before reset or openclaw rebuild
6. **Verify** after every fix: `gateway status` + `doctor`

## Environment

| Variable | Default | Purpose |
|----------|---------|---------|
| `OPENCLAW_HOME` | `~/.openclaw` | OpenClaw data dir |
| `RECLAW_BACKUP_DIR` | `~/claw-backup` | Backup destination |
| `RECLAW_SETUP_FORCE` | `0` | Force setup wizard |
| `RECLAW_DEBUG` | `0` | Verbose logging |
| `RECLAW_DISABLE_WSL` | `0` | Disable WSL2 detection (Windows) |

## OpenClaw Fresh Install

```bash
# 1. Node.js 22+
# macOS/Linux:
curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.39.7/install.sh | bash
source ~/.bashrc && nvm install 22 && nvm use 22
# Windows: winget install Schniz.fnm && fnm install 22 && fnm use 22

# 2. OpenClaw
npm install -g openclaw@latest && openclaw --version

# 3. Configure (~/.openclaw/openclaw.json)
# Set provider (deepseek/anthropic/openai), apiKey, gateway mode+token

# 4. Start
openclaw gateway install && openclaw gateway start
openclaw doctor && openclaw gateway status
```

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Gateway offline | `create` → `gateway repair` → `gateway status` |
| Gateway stuck | Kill process → `gateway start` |
| Config corrupt | `create` → `recover` → `gateway status` |
| Fresh start | `create` → `reset --preview` → `reset --confirm` |
| Token missing | `gateway token show` or `gateway token generate` |
| Unknown | `doctor` → `diagnostics export` |
