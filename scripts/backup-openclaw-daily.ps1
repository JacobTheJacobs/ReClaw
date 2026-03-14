[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidAssignmentToAutomaticVariable', '', Justification = 'False positive in editor diagnostics for this script')]
Param(
  [Parameter(Mandatory = $false)]
  [string]$BackupDir = "",

  [Parameter(Mandatory = $false)]
  [int]$KeepDays = 7,

  [Parameter(Mandatory = $false)]
  [switch]$SkipSecurity,

  [Parameter(Mandatory = $false)]
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($BackupDir)) {
  $BackupDir = Join-Path $HOME 'Backups/openclaw'
}

if ($KeepDays -lt 0) {
  throw '-KeepDays must be zero or greater.'
}

$DateStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$LogFile = Join-Path $BackupDir "backup-$DateStamp.log"
$ArchivePath = Join-Path $BackupDir "openclaw-$DateStamp.tar.gz"

$openclawCommand = Get-Command openclaw -ErrorAction SilentlyContinue
if (-not $openclawCommand -and -not $DryRun) {
  throw 'openclaw command not found in PATH.'
}
$line = "[$(Get-Date -Format o)] [dry-run] openclaw command not found in PATH; showing planned commands only."
if (-not $openclawCommand -and $DryRun) {
  $line | Tee-Object -FilePath $LogFile -Append
}

$line = "[$(Get-Date -Format o)] === OpenClaw daily backup started: $DateStamp ==="
$line | Tee-Object -FilePath $LogFile -Append

$line = "[$(Get-Date -Format o)] Running health check"
$line | Tee-Object -FilePath $LogFile -Append
if ($DryRun) {
  "[dry-run] openclaw health --json" | Tee-Object -FilePath $LogFile -Append
} else {
  & openclaw health --json *>> $LogFile
  if ($LASTEXITCODE -ne 0) {
    throw "Running health check failed with exit code $LASTEXITCODE. See log: $LogFile"
  }
}

$line = "[$(Get-Date -Format o)] Creating verified backup"
$line | Tee-Object -FilePath $LogFile -Append
if ($DryRun) {
  ("[dry-run] openclaw backup create --verify --output `"$ArchivePath`"") | Tee-Object -FilePath $LogFile -Append
} else {
  & openclaw backup create --verify --output $ArchivePath *>> $LogFile
  if ($LASTEXITCODE -ne 0) {
    throw "Creating verified backup failed with exit code $LASTEXITCODE. See log: $LogFile"
  }
}

if (-not $SkipSecurity) {
  $line = "[$(Get-Date -Format o)] Running security audit"
  $line | Tee-Object -FilePath $LogFile -Append
  if ($DryRun) {
    "[dry-run] openclaw security audit --json" | Tee-Object -FilePath $LogFile -Append
  } else {
    & openclaw security audit --json *>> $LogFile
    if ($LASTEXITCODE -ne 0) {
      throw "Running security audit failed with exit code $LASTEXITCODE. See log: $LogFile"
    }
  }
}

$line = "[$(Get-Date -Format o)] Applying retention cleanup (older than $KeepDays days)"
$line | Tee-Object -FilePath $LogFile -Append
$cutoff = (Get-Date).AddDays(-$KeepDays)
$oldBackups = Get-ChildItem -Path $BackupDir -File -ErrorAction SilentlyContinue | Where-Object {
  ($_.Name -match '^openclaw-.*\.tar\.gz$' -or $_.Name -match '^openclaw-.*\.tar\.gz\.enc$' -or $_.Name -match '^openclaw-.*\.zip$') -and
  $_.LastWriteTime -lt $cutoff
}

if ($DryRun) {
  foreach ($item in $oldBackups) {
    ("[dry-run] delete " + $item.FullName) | Tee-Object -FilePath $LogFile -Append
  }
} else {
  foreach ($item in $oldBackups) {
    $line = "[$(Get-Date -Format o)] Deleting old backup: $($item.FullName)"
    $line | Tee-Object -FilePath $LogFile -Append
    Remove-Item -Force -Path $item.FullName -ErrorAction SilentlyContinue
  }
}

$line = "[$(Get-Date -Format o)] === OpenClaw daily backup complete ==="
$line | Tee-Object -FilePath $LogFile -Append
$line = "[$(Get-Date -Format o)] Backup archive: $ArchivePath"
$line | Tee-Object -FilePath $LogFile -Append
$line = "[$(Get-Date -Format o)] Log file: $LogFile"
$line | Tee-Object -FilePath $LogFile -Append
