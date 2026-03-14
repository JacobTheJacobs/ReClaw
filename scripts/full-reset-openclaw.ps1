Param(
  [Parameter(Mandatory = $false)]
  [string]$ReClawRepo = "",

  [Parameter(Mandatory = $false)]
  [string]$OpenClawRepo = "",

  [Parameter(Mandatory = $false)]
  [string]$BackupDir = "",

  [Parameter(Mandatory = $false)]
  [string]$BackupName = "backup.zip",

  [Parameter(Mandatory = $false)]
  [string]$Password = "$env:RECLAW_PASSWORD",

  [Parameter(Mandatory = $false)]
  [switch]$IncludeBrowser,

  [Parameter(Mandatory = $false)]
  [switch]$RemoveOpenClawRepo,

  [Parameter(Mandatory = $false)]
  [switch]$AllowSmallBackup,

  [Parameter(Mandatory = $false)]
  [switch]$Yes
)

$ErrorActionPreference = 'Stop'

function Log([string]$msg) {
  Write-Host "[reset-win] $msg"
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

function Invoke-ExternalCommand([string]$FilePath, [string[]]$Arguments, [string]$StepName) {
  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "$StepName failed with exit code $LASTEXITCODE."
  }
}

function Test-ArchiveContainsPath([string[]]$EntryNames, [string]$PathPrefix) {
  foreach ($name in $EntryNames) {
    if ($name -eq $PathPrefix -or $name -eq "$PathPrefix/" -or $name.StartsWith("$PathPrefix/")) {
      return $true
    }
  }
  return $false
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ReClawRepo)) {
  $ReClawRepo = Split-Path -Parent $scriptDir
}

$workspaceRoot = Split-Path -Parent $ReClawRepo
if ([string]::IsNullOrWhiteSpace($OpenClawRepo)) {
  $OpenClawRepo = Get-DefaultOpenClawRepoPath
}
if ([string]::IsNullOrWhiteSpace($BackupDir)) {
  if (-not [string]::IsNullOrWhiteSpace($env:RECLAW_BACKUP_DIR)) {
    $BackupDir = $env:RECLAW_BACKUP_DIR
  } elseif (-not [string]::IsNullOrWhiteSpace($env:BACKUP_DIR)) {
    $BackupDir = $env:BACKUP_DIR
  } else {
    $BackupDir = Join-Path $workspaceRoot 'claw-backup'
  }
}

if ([string]::IsNullOrWhiteSpace($Password)) {
  $Password = "$env:RECLAW_PASSWORD"
}

if (-not (Test-Path $ReClawRepo)) {
  throw "ReClaw repo not found: $ReClawRepo"
}

if (-not $Yes) {
  Write-Host "This script will:"
  Write-Host "1) Create backup archive at: $(Join-Path $BackupDir $BackupName)"
  Write-Host "2) Remove OpenClaw service/state/CLI leftovers on this machine."
  if ($RemoveOpenClawRepo) {
    Write-Host "3) Remove OpenClaw repository: $OpenClawRepo"
  }
  $reply = Read-Host "Continue? [y/N]"
  if ($reply -notmatch '^[Yy]') {
    Write-Host "Aborted."
    exit 0
  }
}

New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null

Log "Creating backup with ReClaw"
Push-Location $ReClawRepo
try {
  $env:BACKUP_DIR = $BackupDir
  $backupArgs = @('bin/cli.js', 'backup')
  if (-not [string]::IsNullOrWhiteSpace($Password)) {
    $backupArgs += @('--password', $Password)
  } else {
    Log "No backup password provided; creating unencrypted backup"
  }
  if ($IncludeBrowser) {
    $backupArgs += '--include-browser'
  }
  Invoke-ExternalCommand -FilePath 'node' -Arguments $backupArgs -StepName 'Backup command'
} finally {
  Pop-Location
}

$backupSourceDir = Join-Path $ReClawRepo 'backups'
$latestBackup = Get-ChildItem -Path $backupSourceDir -File -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match '^openclaw_backup_.*\.(zip|tar\.gz|tar\.gz\.enc)$' } |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1

if (-not $latestBackup) {
  throw "No backup archive generated under $backupSourceDir"
}

$latestSize = $latestBackup.Length
if (-not $AllowSmallBackup -and $latestSize -lt 10MB) {
  throw "Refusing to continue: generated backup is too small ($latestSize bytes). Use -AllowSmallBackup to override."
}

$lowerName = $latestBackup.Name.ToLowerInvariant()
if (-not $AllowSmallBackup) {
  if ($lowerName.EndsWith('.zip')) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($latestBackup.FullName)
    try {
      $entryNames = @($zip.Entries | ForEach-Object { $_.FullName.TrimStart('.', '/') })
    } finally {
      $zip.Dispose()
    }

    $missing = @()
    if (-not ($entryNames -contains 'manifest.json')) {
      $missing += 'manifest.json'
    }
    if (-not ($entryNames | Where-Object { $_ -eq 'openclaw.json' -or $_ -like '*/openclaw.json' } | Select-Object -First 1)) {
      $missing += 'openclaw.json'
    }
    if (-not (Test-ArchiveContainsPath -EntryNames $entryNames -PathPrefix 'workspaces')) {
      $missing += 'workspaces'
    }
    if (-not (Test-ArchiveContainsPath -EntryNames $entryNames -PathPrefix 'plugins')) {
      $missing += 'plugins'
    }
    if (-not (Test-ArchiveContainsPath -EntryNames $entryNames -PathPrefix 'credentials')) {
      $missing += 'credentials'
    }

    if ($missing.Count -gt 0) {
      throw "Refusing to continue: backup appears incomplete (missing $($missing -join ', ')). Use -AllowSmallBackup to override."
    }
  } elseif ($lowerName.EndsWith('.tar.gz')) {
    $tarList = & tar -tzf $latestBackup.FullName 2>$null
    if ($LASTEXITCODE -ne 0) {
      throw "Refusing to continue: could not read tar.gz backup entries. Use -AllowSmallBackup to override."
    }

    $missing = @()
    if (-not ($tarList | Where-Object { $_ -eq 'manifest.json' } | Select-Object -First 1)) {
      $missing += 'manifest.json'
    }
    if (-not ($tarList | Where-Object { $_ -match '(^|/)openclaw\.json$' } | Select-Object -First 1)) {
      $missing += 'openclaw.json'
    }
    if (-not ($tarList | Where-Object { $_ -match '(^|/)workspaces(/|$)' } | Select-Object -First 1)) {
      $missing += 'workspaces'
    }
    if (-not ($tarList | Where-Object { $_ -match '(^|/)plugins(/|$)' } | Select-Object -First 1)) {
      $missing += 'plugins'
    }
    if (-not ($tarList | Where-Object { $_ -match '(^|/)credentials(/|$)' } | Select-Object -First 1)) {
      $missing += 'credentials'
    }

    if ($missing.Count -gt 0) {
      throw "Refusing to continue: backup appears incomplete (missing $($missing -join ', ')). Use -AllowSmallBackup to override."
    }
  } else {
    Log "Skipping archive content verification for encrypted archive."
  }
}

$target = Join-Path $BackupDir $BackupName
Copy-Item -Force -Path $latestBackup.FullName -Destination $target
Log "Backup saved to: $target"

Log "Stopping OpenClaw managed service (best effort)"
try { & openclaw gateway stop | Out-Null } catch {}
try { & openclaw gateway uninstall | Out-Null } catch {}
try { & openclaw uninstall --all --yes --non-interactive | Out-Null } catch {}

Log "Stopping and removing scheduled task service"
try { schtasks /Delete /F /TN 'OpenClaw Gateway' | Out-Null } catch {}
try {
  $tasks = schtasks /Query /FO LIST | Select-String -Pattern '^TaskName:\\.*OpenClaw Gateway'
  foreach ($t in $tasks) {
    $name = ($t.ToString() -replace '^TaskName:\\', '').Trim()
    if ($name) {
      schtasks /Delete /F /TN $name | Out-Null
    }
  }
} catch {}

Log "Stopping local OpenClaw processes"
try {
  $openclawProcesses = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    ($_.Name -match '^node(\.exe)?$') -and
    ($_.CommandLine -match 'openclaw\.mjs\s+gateway' -or
      $_.CommandLine -match '\bopenclaw-gateway\b' -or
      $_.CommandLine -match 'npm\s+exec\s+openclaw\s+gateway' -or
      $_.CommandLine -match 'pnpm\s+openclaw\s+gateway')
  }
  foreach ($proc in $openclawProcesses) {
    Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
  }
} catch {}

try {
  $portPids = Get-NetTCPConnection -LocalPort 18789 -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique
  foreach ($pid in $portPids) {
    if ($pid) {
      Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue
    }
  }
} catch {}

Log "Removing OpenClaw state"
Remove-Item -Recurse -Force (Join-Path $env:USERPROFILE '.openclaw') -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $env:USERPROFILE '.openclaw-workspace') -ErrorAction SilentlyContinue
Get-ChildItem -Path $env:USERPROFILE -Filter '.openclaw-*' -Directory -ErrorAction SilentlyContinue | ForEach-Object {
  Remove-Item -Recurse -Force $_.FullName -ErrorAction SilentlyContinue
}

Log "Removing global OpenClaw CLI (best effort)"
try { npm rm -g openclaw | Out-Null } catch {}
try {
  if (Get-Command pnpm -ErrorAction SilentlyContinue) {
    $pnpmGlobalBin = Join-Path $env:LOCALAPPDATA 'pnpm'
    $pathHasPnpmGlobalBin = ($env:Path -split ';' | Where-Object { $_ -ieq $pnpmGlobalBin }).Count -gt 0
    if ($pathHasPnpmGlobalBin) {
      & pnpm remove -g openclaw *> $null
    } else {
      Log "Skipping pnpm global cleanup (pnpm global bin dir is not in PATH)."
    }
  }
} catch {}
try { bun remove -g openclaw | Out-Null } catch {}

if ($RemoveOpenClawRepo -and (Test-Path $OpenClawRepo)) {
  Log "Removing OpenClaw git clone: $OpenClawRepo"
  Log "Delete progress bytes are logical estimates and can over-report with many files/hardlinks."
  Remove-Item -Recurse -Force $OpenClawRepo -ErrorAction SilentlyContinue
}

Log "Reset complete"
Log "Backup preserved at $target"
