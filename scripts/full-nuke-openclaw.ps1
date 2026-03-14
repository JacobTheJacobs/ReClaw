Param(
  [Parameter(Mandatory = $false)]
  [string]$ReClawRepo = "",

  [Parameter(Mandatory = $false)]
  [string]$OpenClawRepo = "",

  [Parameter(Mandatory = $false)]
  [switch]$RemoveOpenClawRepo,

  [Parameter(Mandatory = $false)]
  [switch]$Yes
)

$ErrorActionPreference = 'Stop'

function Log([string]$msg) {
  Write-Host "[nuke-win] $msg"
}

function Get-DefaultOpenClawRepoPath() {
  $root = if (-not [string]::IsNullOrWhiteSpace($env:PUBLIC)) {
    Join-Path $env:PUBLIC 'openclaw-src'
  } elseif (-not [string]::IsNullOrWhiteSpace($env:SystemDrive)) {
    Join-Path $env:SystemDrive 'openclaw-src'
  } else {
    'C:\openclaw-src'
  }

  return Join-Path $root 'openclaw'
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ReClawRepo)) {
  $ReClawRepo = Split-Path -Parent $scriptDir
}

if ([string]::IsNullOrWhiteSpace($OpenClawRepo)) {
  $OpenClawRepo = Get-DefaultOpenClawRepoPath
}

if (-not (Test-Path $ReClawRepo)) {
  throw "ReClaw repo not found: $ReClawRepo"
}

if (-not $Yes) {
  Write-Host "This script will:"
  Write-Host "1) Remove OpenClaw service/state/CLI leftovers on this machine."
  if ($RemoveOpenClawRepo) {
    Write-Host "2) Remove OpenClaw repository: $OpenClawRepo"
  }
  $reply = Read-Host "Continue? [y/N]"
  if ($reply -notmatch '^[Yy]') {
    Write-Host "Aborted."
    exit 0
  }
}

Log "Stopping OpenClaw services/processes"
try { & openclaw gateway stop | Out-Null } catch {}
try { & openclaw gateway uninstall | Out-Null } catch {}
try { & openclaw uninstall --all --yes --non-interactive | Out-Null } catch {}

Get-Process openclaw* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Log "Removing OpenClaw state/config leftovers"
Remove-Item -Recurse -Force "$env:USERPROFILE\.openclaw" -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "$env:USERPROFILE\.openclaw-workspace" -ErrorAction SilentlyContinue
Get-ChildItem -Path "$env:USERPROFILE" -Filter ".openclaw-*" -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Log "Cleaning global CLI installs (best effort)"
try { npm rm -g openclaw | Out-Null } catch {}
try { pnpm remove -g openclaw | Out-Null } catch {}
try { bun remove -g openclaw | Out-Null } catch {}

if ($RemoveOpenClawRepo -and (Test-Path $OpenClawRepo)) {
  Log "Removing OpenClaw git clone: $OpenClawRepo"
  Remove-Item -Recurse -Force $OpenClawRepo
}

Log "Nuke complete"
