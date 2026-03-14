Param(
  [Parameter(Mandatory = $false)]
  [string]$Password = "$env:RECLAW_PASSWORD",

  [Parameter(Mandatory = $false)]
  [string]$ReClawRepo = "",

  [Parameter(Mandatory = $false)]
  [string]$WorkspaceRoot = "",

  [Parameter(Mandatory = $false)]
  [string]$OpenClawRepo = "",

  [Parameter(Mandatory = $false)]
  [string]$OpenClawRepoUrl = "https://github.com/openclaw/openclaw.git",

  [Parameter(Mandatory = $false)]
  [string]$BackupDir = "",

  [Parameter(Mandatory = $false)]
  [string]$BackupName = "backup.zip",

  [Parameter(Mandatory = $false)]
  [switch]$Yes,

  [Parameter(Mandatory = $false)]
  [switch]$AllowSmallBackup
)

$ErrorActionPreference = 'Stop'

function Log([string]$msg) {
  Write-Host "[test-recovery-win] $msg"
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
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
  $WorkspaceRoot = Split-Path -Parent $ReClawRepo
}
if ([string]::IsNullOrWhiteSpace($OpenClawRepo)) {
  $OpenClawRepo = Get-DefaultOpenClawRepoPath
}
if ([string]::IsNullOrWhiteSpace($BackupDir)) {
  $BackupDir = Join-Path $WorkspaceRoot 'claw-backup'
}

if ([string]::IsNullOrWhiteSpace($Password)) {
  $Password = "$env:RECLAW_PASSWORD"
}

if ([string]::IsNullOrWhiteSpace($Password)) {
  throw "Missing -Password (or set RECLAW_PASSWORD)."
}

$archivePath = Join-Path $BackupDir $BackupName

Log "Step 1/5: Backup + full reset (includes browser)"
$resetArgs = @{
  ReClawRepo = $ReClawRepo
  OpenClawRepo = $OpenClawRepo
  BackupDir = $BackupDir
  BackupName = $BackupName
  Password = $Password
  IncludeBrowser = $true
  RemoveOpenClawRepo = $true
}
if ($AllowSmallBackup) {
  $resetArgs.AllowSmallBackup = $true
}
if ($Yes) {
  $resetArgs.Yes = $true
}
& (Join-Path $scriptDir 'full-reset-openclaw.ps1') @resetArgs
if ($LASTEXITCODE -ne 0) {
  throw "full-reset-openclaw.ps1 failed with exit code $LASTEXITCODE"
}

Log "Step 2/5: Verify OpenClaw state is empty"
Push-Location $ReClawRepo
try {
  & node scripts/verify-openclaw-state.js empty
  if ($LASTEXITCODE -ne 0) {
    throw "verify-openclaw-state.js empty failed with exit code $LASTEXITCODE"
  }
} finally {
  Pop-Location
}

Log "Step 3/5: Reclone/install/build/restore/start gateway"
$recoverArgs = @{
  Password = $Password
  ReClawRepo = $ReClawRepo
  WorkspaceRoot = $WorkspaceRoot
  OpenClawRepo = $OpenClawRepo
  ArchivePath = $archivePath
  OpenClawRepoUrl = $OpenClawRepoUrl
}
& (Join-Path $scriptDir 'recover-openclaw-local-windows.ps1') @recoverArgs
if ($LASTEXITCODE -ne 0) {
  throw "recover-openclaw-local-windows.ps1 failed with exit code $LASTEXITCODE"
}

Log "Step 4/5: Verify restored payload"
Push-Location $ReClawRepo
try {
  & node scripts/verify-openclaw-state.js restored
  if ($LASTEXITCODE -ne 0) {
    throw "verify-openclaw-state.js restored failed with exit code $LASTEXITCODE"
  }
} finally {
  Pop-Location
}

Log "Step 5/5: Verify gateway health"
$health = Invoke-WebRequest -Uri 'http://127.0.0.1:18789/healthz' -UseBasicParsing -TimeoutSec 10
if ($health.StatusCode -lt 200 -or $health.StatusCode -ge 300) {
  throw "Gateway health check failed with status $($health.StatusCode)."
}

Log "Windows recovery drill complete"
Log "Archive used: $archivePath"
Log "Gateway health: ok"
