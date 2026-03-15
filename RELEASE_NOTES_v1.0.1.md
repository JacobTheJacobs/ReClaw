# ReClaw v1.0.1 Release Notes

Release focus: Windows and macOS desktop builds via GitHub Releases. No npm publish for this release.

## Highlights
- Desktop backup + restore flow for OpenClaw on Win/Mac.
- One place to recover, repair, and migrate configs across machines.
- Safer defaults and clearer debug controls.

## Changes
- Removed postinstall side effects (no automatic config mutation on install).
- Gateway dashboard URL copy is now opt-in (`--copy`/`--open` or `RECLAW_COPY_DASHBOARD_URL=1`).
- Live integration tests are opt-in (`RUN_LIVE_TESTS=1`).
- Added `RECLAW_DEBUG=1` for verbose logs and path details.
- CI runs `npm test` on Windows + Ubuntu.
- New always-visible guidance bar with recommended actions.
- Windows: added Kill Gateway Processes and Disable Gateway Autostart actions; combined Install + Start action for stubborn gateways.
- Auto gateway start is disabled by default to avoid surprise console flashes; use the explicit Gateway actions instead.
- Windows gateway fixes: gateway run now launches detached (no hanging action), PowerShell-based kill avoids `Select-Object` errors, and gateway actions auto-install the OpenClaw CLI when missing. Guidance now calls out spawn EINVAL/ENOENT and suggests the exact fix.

## Downloads
- Windows: NSIS installer + portable build (x64).
- macOS: DMG + ZIP (arm64).

## Known Limitations
- Builds are unsigned; macOS Gatekeeper and Windows SmartScreen may warn.
- macOS builds target arm64 only.

## Quick Use
1. Download the installer from GitHub Releases.
2. Open ReClaw; it finds OpenClaw automatically.
3. Run a backup, then test restore with `--dry-run` before a real restore.
