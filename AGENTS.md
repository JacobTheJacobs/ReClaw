# ReClaw — Agent Instructions

> For GitHub Copilot, Codex, and any agent reading AGENTS.md

Manage OpenClaw: backup, restore, repair, monitor, schedule, setup.

## Setup

```bash
dotnet --version   # .NET SDK 10.0+
git --version
cd <repo-root> && dotnet build ReClaw.slnx -c Release
CLI="dotnet run --project <repo-root>/src/ReClaw.Cli/ReClaw.Cli.csproj --"
```

## CRITICAL: Space-separated subcommands, NOT hyphens

```bash
$CLI gateway status        # NOT: gateway-status
$CLI backup create         # NOT: backup-create
$CLI create                # root alias for backup
$CLI restore -s <path>     # NOT: restore-archive
$CLI reset                 # NOT: nuke
```

## Decision Tree

```
1. Prerequisites → dotnet 10+, git
2. OpenClaw installed? → NO → $CLI openclaw rebuild --confirm-destructive
3. $CLI gateway status → healthy ("Runtime: running" + "RPC probe: ok") or broken
4. Broken → $CLI create → $CLI gateway repair → $CLI gateway status
5. Config corrupt → $CLI create → $CLI recover → $CLI gateway status
6. Escalate: doctor → fix → gateway repair → recover → reset
```

## Commands

### Root
| Command | Destructive | Notes |
|---------|:-----------:|-------|
| `create [-p pass]` | | Backup (optional encryption) |
| `verify -s <path>` | | Check backup integrity |
| `restore -s <path> [--preview]` | YES | Restore from archive |
| `doctor [--repair] [--deep] [--force]` | | Health check |
| `fix [--force]` | YES | Deep fix with snapshot |
| `recover` | YES | Escalating pipeline |
| `rollback -s <path> --confirm-rollback` | YES | Rollback from snapshot |
| `reset --preview` then `--confirm` | YES | Wipe local data |
| `status` | | Paths + inventory |
| `dashboard` | | Open web UI |
| `cleanup-related [--apply --confirm]` | YES | Clean artifacts |
| `diagnostics export` | | Debug bundle |
| `version` | | CLI version |
| `action-list` | | List all 91 actions |
| `action-schema [--id <id>]` | | JSON schema |

### Backup
| Command | Notes |
|---------|-------|
| `backup create [--scope config] [-p pass]` | |
| `backup verify -s <path>` | |
| `backup list` | |
| `backup restore -s <path> [--preview]` | |
| `backup diff --left <a> --right <b>` | |
| `backup export [--scope config+creds+sessions]` | |
| `backup prune [--apply] [--keep-last 5] [--older-than 30d]` | Dry-run default |
| `backup schedule create [--kind daily\|weekly\|monthly\|cron]` | |
| `backup schedule list` | |
| `backup schedule remove` | |

### Gateway
| Command | Notes |
|---------|-------|
| `gateway status` | **Run first, always** |
| `gateway start` | |
| `gateway stop` | Destructive |
| `gateway repair` | 9-step auto-escalating ladder |
| `gateway logs` | Cancellable (Ctrl+C) |
| `gateway url` | Dashboard URL + token |
| `gateway token show [--reveal]` | Masked by default |
| `gateway token generate` | |
| `gateway browser-diagnostics` | |

### OpenClaw
| Command | Notes |
|---------|-------|
| `openclaw rebuild --confirm-destructive` | Full rebuild |
| `openclaw cleanup-related [--apply --confirm]` | Clean artifacts |

## Output Parsing

- **gateway status**: "Runtime: running" + "RPC probe: ok" = healthy
- **doctor**: exit 0 = pass; "FAIL"/"error" = run fix
- **create**: prints backup path on success

## Reset Modes (`--reset-mode`)

`preserve-backups` (default) · `preserve-config` · `preserve-cli` · `full`

## Safety Rules

1. Always `gateway status` before/after fixes
2. Always `create` before destructive ops
3. Always `--preview` before restore/reset
4. Escalate: doctor → fix → gateway repair → recover → reset
5. Ask user before reset or rebuild

## Environment

| Variable | Default |
|----------|---------|
| `OPENCLAW_HOME` | `~/.openclaw` |
| `RECLAW_BACKUP_DIR` | `~/claw-backup` |
