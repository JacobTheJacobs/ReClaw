<div align="center">

<br/>

# ReClaw

### Gateway broken? Config corrupt? Backup gone?<br/>Fix your [OpenClaw](https://github.com/nicepkg/openclaw) in one click.

<br/>

<a href="https://github.com/JacobTheJacobs/ReClaw/releases"><img src="https://img.shields.io/badge/Download-Windows_x64-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Windows" /></a>&nbsp;&nbsp;
<a href="https://github.com/JacobTheJacobs/ReClaw/releases"><img src="https://img.shields.io/badge/Download-macOS_arm64-000000?style=for-the-badge&logo=apple&logoColor=white" alt="macOS" /></a>&nbsp;&nbsp;
<a href="https://github.com/JacobTheJacobs/ReClaw/releases"><img src="https://img.shields.io/badge/Download-Linux_x64-FCC624?style=for-the-badge&logo=linux&logoColor=black" alt="Linux" /></a>

<br/><br/>

<strong>English</strong> | <a href="README.zh-CN.md">简体中文</a>

<br/><br/>

<img width="2740" height="1568" alt="ReClaw" src="https://github.com/user-attachments/assets/6898ee02-5a5c-4e5c-bd15-cb4a6543ddbc" />

<br/>

</div>

---

<br/>

<div align="center">

### The problem

OpenClaw breaks. Gateway goes offline. Config gets corrupted.<br/>
Backups are manual. Recovery is guesswork. You lose hours.

### The fix

ReClaw. One app. **91 actions.** Backup, restore, repair, monitor, schedule.<br/>
Desktop GUI or CLI. Works on every platform. Paste the agent prompt into any AI agent<br/>
and it manages OpenClaw for you automatically.

</div>

<br/>

---

<br/>

<div align="center">
<table>
<tr>
<td width="50%">

<img width="559" alt="Main UI" src="https://github.com/user-attachments/assets/665a12c4-bad7-4b31-bd88-cb2ad2d6dcac" />

</td>
<td width="50%">

<img width="552" alt="Actions" src="https://github.com/user-attachments/assets/03846f2f-769c-469d-8fe6-b751c5cd0d19" />

</td>
</tr>
</table>
</div>

<br/>

---

## What it does

| Problem | ReClaw fix | Command |
|---------|-----------|---------|
| Gateway offline | 9-step auto-escalating repair | `gateway repair` |
| Config corrupted | Restore from latest backup | `recover` |
| Need a backup | Encrypted snapshot, one command | `create -p <pass>` |
| Something's wrong | Health check + auto-fix | `doctor --repair` |
| Total disaster | Full recovery pipeline | `recover` |
| Scheduled backups | Daily, weekly, or cron | `backup schedule create` |
| Need to rollback | Restore any snapshot | `rollback -s <path>` |
| Fresh install | Rebuild from scratch | `openclaw rebuild` |

---

## Quick start

**Download** from [Releases](https://github.com/JacobTheJacobs/ReClaw/releases) and run. Or build from source:

```bash
git clone https://github.com/JacobTheJacobs/ReClaw.git && cd ReClaw
dotnet build ReClaw.slnx -c Release
dotnet run --project src/ReClaw.Desktop/ReClaw.Desktop.csproj
```

Requires [.NET 10+](https://dotnet.microsoft.com/download).

---

## Agent Prompt

> Paste into **any AI agent** — Claude Code, Copilot, Cursor, Codex, Windsurf — and it can manage your entire OpenClaw installation. 91 actions, verified against source.

```bash
# ReClaw — OpenClaw Management
# Paste into any AI agent. Requires .NET 10+ and Git.

# Setup
git clone https://github.com/JacobTheJacobs/ReClaw.git /tmp/reclaw
cd /tmp/reclaw && dotnet build ReClaw.slnx -c Release
CLI="dotnet run --project /tmp/reclaw/src/ReClaw.Cli/ReClaw.Cli.csproj --"

# CRITICAL: space-separated subcommands, NOT hyphens
$CLI gateway status       # NOT: gateway-status
$CLI backup create        # NOT: backup-create
$CLI create               # root alias for backup
$CLI restore -s <path>    # NOT: restore-archive

# Safety: always gateway status before/after, always create before destructive ops
# Escalate: doctor → fix → gateway repair → recover → reset

# Decision tree
# 1. OpenClaw installed? → NO → $CLI openclaw rebuild --confirm-destructive
# 2. $CLI gateway status → "Runtime: running" + "RPC probe: ok" = healthy
# 3. Broken → $CLI create → $CLI gateway repair → $CLI gateway status
# 4. Config corrupt → $CLI create → $CLI recover → $CLI gateway status

# Core commands
$CLI create [-p pass]                   # Backup (optional encryption)
$CLI restore -s <path> [--preview]      # Restore from backup
$CLI doctor [--repair] [--deep]         # Health check
$CLI fix [--force]                      # Deep fix with snapshot
$CLI recover                            # Escalating recovery pipeline
$CLI gateway status                     # Check health (RUN FIRST)
$CLI gateway start                      # Start gateway
$CLI gateway stop                       # Stop gateway
$CLI gateway repair                     # 9-step repair ladder
$CLI gateway logs                       # Tail logs (Ctrl+C)
$CLI dashboard                          # Open web dashboard
$CLI openclaw rebuild                   # Rebuild OpenClaw (destructive)
$CLI reset --preview / --confirm        # Reset local data (destructive)
$CLI diagnostics export                 # Debug bundle
$CLI backup list                        # List backups
$CLI backup prune [--apply]             # Prune old backups
$CLI backup schedule create             # Schedule backups (daily/weekly/cron)
$CLI rollback -s <path>                 # Rollback from snapshot
$CLI gateway token show [--reveal]      # Show gateway token
$CLI gateway token generate             # New token
$CLI action-list                        # List all 91 internal actions
$CLI action-schema --id <action-id>     # Export action JSON schema

# Output: gateway status → "Runtime: running" + "RPC probe: ok" = healthy
# Reset modes: preserve-backups (default), preserve-config, preserve-cli, full
```

<details>
<summary><strong>Full command reference (91 actions)</strong></summary>
<br/>

```bash
# Root
create, verify, restore, doctor, fix, recover, rollback,
reset, status, dashboard, cleanup-related, version,
diagnostics export, action-list, action-schema

# Backup
backup create/verify/list/restore/diff/export/prune
backup schedule create/list/remove

# Gateway
gateway status/start/stop/repair/logs/url/browser-diagnostics
gateway token show/generate

# OpenClaw
openclaw rebuild, openclaw cleanup-related

# Key options
--preview      Preview without applying (restore, reset, rollback, prune)
--confirm      Confirm reset
--confirm-destructive  Confirm rebuild
--confirm-rollback     Confirm rollback
--json         JSON output
-p <password>  Encrypt/decrypt backup
--repair       Doctor with repair
--force        Force repair
--deep         Deep health check
--reset-mode   preserve-backups|preserve-config|preserve-cli|full
--scope        config|config+creds+sessions|full
--keep-last N  Prune: keep last N (default: 5)
--older-than   Prune: age threshold (default: 30d)
--kind         Schedule: daily|weekly|monthly|cron
```

</details>

---

## How it works

```
You                          ReClaw                         OpenClaw
 │                             │                              │
 ├─ "gateway is broken" ──────►│                              │
 │                             ├─ create (backup) ───────────►│
 │                             ├─ gateway repair ────────────►│
 │                             │  ├─ mode fix                 │
 │                             │  ├─ doctor                   │
 │                             │  ├─ session cleanup          │
 │                             │  ├─ reinstall service        │
 │                             │  ├─ start + poll             │
 │                             │  ├─ stability check          │
 │                             │  ├─ lock cleanup             │
 │                             │  ├─ process kill             │
 │                             │  └─ detached fallback        │
 │                             ├─ gateway status ────────────►│
 │◄─ "fixed, gateway healthy" ─┤                              │
```

---

<br/>

<div align="center">

<a href="https://github.com/JacobTheJacobs/ReClaw/releases"><strong>Download</strong></a>&nbsp;&nbsp;&nbsp;&middot;&nbsp;&nbsp;&nbsp;<a href="https://github.com/JacobTheJacobs/ReClaw/issues"><strong>Issues</strong></a>&nbsp;&nbsp;&nbsp;&middot;&nbsp;&nbsp;&nbsp;<a href="https://github.com/nicepkg/openclaw"><strong>OpenClaw</strong></a>

<br/>

MIT &copy; [JacobTheJacobs](https://github.com/JacobTheJacobs)

<br/>

</div>
