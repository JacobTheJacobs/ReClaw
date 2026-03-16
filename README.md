# ReClaw

![Windows](https://img.shields.io/badge/Windows-x64-blue?style=for-the-badge)
![macOS](https://img.shields.io/badge/macOS-arm64-black?style=for-the-badge)

<img width="559" height="677" alt="Image" src="https://github.com/user-attachments/assets/665a12c4-bad7-4b31-bd88-cb2ad2d6dcac" />
<img width="552" height="670" alt="Image" src="https://github.com/user-attachments/assets/03846f2f-769c-469d-8fe6-b751c5cd0d19" />

OpenClaw backup and restore. Because things break.

<img width="546" height="700" alt="Image" src="https://github.com/user-attachments/assets/12535a56-6783-4e91-8dd3-d48bc5ed0eb9" />

> **Safety first (experimental tool):** ReClaw is best-effort automation. Always keep your own manual backup (outside of ReClaw) before running installs, nukes, or restores. If anything goes wrong, you should be able to restore from that independent backup without ReClaw.


---

## Quick Start

**Desktop (recommended)**
- Download the latest release and open it; ReClaw auto-detects OpenClaw.
- Windows (1.0.1 tested on Win10/11 x64): flashing should be minimal (onboarding). If a console flashes:
  - “Disable Gateway Autostart” → “Kill Gateway Processes” → “OC Gateway Install + Start”, **or**
  - Use “OC Gateway Run (No Autostart)” to skip the login task entirely.
- macOS: arm64 DMG/zip tested.
- Guidance bar: always shows the next best step and highlights the button to click (Gateway Start, Backup, Doctor Repair, etc.).
- ReClaw never auto-starts the gateway. Start it yourself with “OC Gateway Install + Start” (or “Run (No Autostart)” on Windows).
- Gateway offline or OpenClaw missing? Click “Install OpenClaw CLI” first.
- Dashboard: start the gateway first, then click 🧭 Open Dashboard to avoid flash-on-start windows.

**Seven quick commands (in the UI)**
1. Install OpenClaw CLI
2. OC Gateway Install + Start
3. OC Gateway Run (No Autostart)  *(Windows: no login task, fewer flashes)*
4. Kill Gateway Processes
5. Disable Gateway Autostart
6. OC Backup Create
7. OC Doctor Fix

**Debug cheatsheet (click order)**
- Gateway offline / ECONNREFUSED: “Install OpenClaw CLI” → “OC Gateway Install + Start”; if still offline, “Kill Gateway Processes” → “OC Gateway Run (No Autostart)” → “Refresh”.
- Flashing console on Windows: “Disable Gateway Autostart” → “Kill Gateway Processes” → “OC Gateway Run (No Autostart)”. Prefer login task? follow with “OC Gateway Install + Start”.
- Token missing in Control UI: click 🧭 Open Dashboard, copy the tokenized URL it prints, paste the token into Control UI settings.
- Still stuck: run “OC Gateway Status”, open the latest log the hint bar mentions, share that log when asking for help.

Source (CLI/dev):
```bash
git clone https://github.com/JacobTheJacobs/ReClaw.git && cd ReClaw && npm install
npm run desktop:open   # UI
./reclaw --help        # CLI
```

---

## For Humans

### Desktop App

Download from [Releases](https://github.com/JacobTheJacobs/ReClaw/releases). Open it. It finds OpenClaw on its own. If OpenClaw isn't installed, it asks once and handles it.

```bash
npm run desktop:pack:mac   # build DMG
npm run desktop:pack:win   # build EXE
```

### Backup

```bash
./reclaw backup create                         # saves everything
./reclaw backup create --password "secret123"  # saves everything, locked
./reclaw backup create --dry-run --json        # describes what it would do, does nothing
```

Desktop app defaults to `~/claw-backup`. Override with `RECLAW_BACKUP_DIR` or `BACKUP_DIR`.

### Restore

```bash
./reclaw restore path/to/backup.zip                    # puts it back
./reclaw restore --password "secret123" backup.zip     # puts it back, was locked
./reclaw backup restore --verify path/to/backup.tar.gz # checks first, then puts it back
```

Password only needed if you used one. ReClaw figures it out.

### Other Commands

```bash
./reclaw backup list                 # see what you have
./reclaw backup prune --keep-last 5  # delete the old ones
./reclaw doctor --repair             # something is wrong, fix it
./reclaw gateway restart             # gateway not running, now it is
npm run dashboard:open               # open dashboard
```

### Gateway offline on Windows? (also stops flashing consoles)
1. If you want **no login task / minimal flashing**, use **OC Gateway Run (No Autostart)** (it runs `openclaw gateway run --port 18789` detached).
2. If you prefer the default login task, use **OC Gateway Install + Start**.
3. If the gateway gets stuck: **Disable Gateway Autostart** → **Kill Gateway Processes** → rerun your chosen start/run action.
4. Hit **Refresh**. If still offline, open gateway logs and check port 18789.

---

## For Agents

**Repo:** `https://github.com/JacobTheJacobs/ReClaw` · **Install:** Download from Releases or clone + `npm install` (source)

### Commands

| Command | What it does |
|---------|-------------|
| `./reclaw backup create [--password P] [--format zip\|tar.gz]` | Create backup |
| `./reclaw backup verify <archive>` | Verify integrity |
| `./reclaw restore [--password P] <archive>` | Restore backup |
| `./reclaw backup list [--json]` | List backups |
| `./reclaw backup prune --keep-last N` | Remove old backups |
| `./reclaw doctor --repair --non-interactive` | Fix OpenClaw |
| `./reclaw gateway restart` | Restart gateway |
| `./reclaw status --deep` | Full status |

### Encryption

Optional at backup time. At restore time, ReClaw checks whether the archive is encrypted and only requires a password if it is. Formats: `.zip` (ZipCrypto), `.tar.gz.enc` (AES-256-GCM).

### Environment Variables

| Variable | Purpose |
|----------|---------|
| `RECLAW_PASSWORD` | Default password |
| `OPENCLAW_HOME` | Override data directory |
| `RECLAW_BACKUP_DIR` | Override backup destination (preferred) |
| `BACKUP_DIR` | Override backup destination (fallback) |
| `RECLAW_SKIP_GATEWAY_STOP=1` | Skip gateway stop on restore |
| `RECLAW_SKIP_GATEWAY_RESTART=1` | Skip gateway restart after restore |
| `RECLAW_FORCE_SETUP_WIZARD=1` | Force setup wizard (testing) |
| `RECLAW_DEBUG=1` | Verbose logs (prints more detail/paths) |
| `RECLAW_COPY_DASHBOARD_URL=1` | Copy dashboard URL to clipboard when running dashboard script |
| `RECLAW_PRINT_DASHBOARD_URL=1` | Print dashboard URL after restore (off by default) |

---

## Testing

```bash
npm test
RUN_LIVE_TESTS=1 npm test          # opt-in: runs real OpenClaw live tests (slow)
npm run test:verify-openclaw:mac   # needs live OpenClaw
npm run test:integration:docker    # needs Docker
```

---

## Roadmap

### v1.0.x — Minor fixes
- [x] Hide PowerShell flashes on Windows actions
- [x] Gateway helper: stop using `cmd /c` (reduces stray console popups)
- [x] Windows gateway loop breaker: Disable Autostart + Kill stuck processes actions
- [ ] Improve gateway status retry after reset/nuke
- [ ] Clearer error copy for missing global tools

### v1.1 — Polish
- [ ] Auto-update check on launch
- [ ] Backup progress bar (streaming output → real progress)
- [ ] Tray icon — run in background, backup from menu bar
- [ ] Keyboard shortcuts for common actions

### v1.2 — Native Apps
- [ ] **macOS:** Rewrite UI in SwiftUI — drop Electron (~150MB → ~5MB), faster launch, proper macOS feel
- [ ] **Windows:** Rewrite in WinUI 3 / .NET MAUI — native installer, no Node.js dependency
- [ ] Shared core stays in Node.js CLI — native apps shell out to it, no logic duplication
- [ ] Code-signed builds for both platforms (no "unverified developer" warnings)

### v1.3 — Features
- [ ] Scheduled backups (daily/weekly cron, configurable retention)
- [ ] Backup diff viewer — see what changed between two archives
- [ ] Multi-instance support — manage multiple OpenClaw profiles
- [ ] Restore preview — show what will be overwritten before committing
- [ ] Cloud backup destination (S3, Dropbox, local NAS)

### v1.4 — Hardening
- [ ] End-to-end test suite for packaged DMG/EXE (not just source)
- [ ] Crash reporter
- [ ] Offline mode detection — graceful fallback when gateway is unreachable
- [ ] Windows: WSL2 path normalization for all script calls

(https://github.com/JacobTheJacobs/ReClaw/issues)
[![GitHub issues](https://img.shields.io/github/issues/JacobTheJacobs/ReClaw?style=for-the-badge)]
---

## License

MIT © [JacobTheJacobs](https://github.com/JacobTheJacobs)
