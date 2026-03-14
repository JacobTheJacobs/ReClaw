Param(
  [Parameter(Mandatory = $false)]
  [string]$ReClawRepo = "",

  [Parameter(Mandatory = $false)]
  [string]$WorkspaceRoot = "",

  [Parameter(Mandatory = $false)]
  [string]$OpenClawRepo = "",

  [Parameter(Mandatory = $false)]
  [string]$OpenClawRepoUrl = "https://github.com/openclaw/openclaw.git"
)

$ErrorActionPreference = 'Stop'

function Log([string]$msg) {
  Write-Host "[fresh-install-win] $msg"
}

function Invoke-ExternalCommand([string]$FilePath, [string[]]$Arguments, [string]$StepName) {
  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "$StepName failed with exit code $LASTEXITCODE."
  }
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

function Get-WindowsPnpmShimRoot() {
  if (-not [string]::IsNullOrWhiteSpace($env:PUBLIC)) {
    return Join-Path $env:PUBLIC 'openclaw-tools'
  }

  if (-not [string]::IsNullOrWhiteSpace($env:SystemDrive)) {
    return Join-Path $env:SystemDrive 'openclaw-tools'
  }

  return 'C:\openclaw-tools'
}

function Resolve-PnpmRunner() {
  $corepack = Get-Command corepack -CommandType Application -ErrorAction SilentlyContinue
  if ($corepack) {
    return @{ FilePath = $corepack.Source; Prefix = @('pnpm') }
  }

  $shimRoot = Get-WindowsPnpmShimRoot
  $pnpmCmd = @(Get-Command pnpm.cmd -CommandType Application -ErrorAction SilentlyContinue) |
    Where-Object { $_.Source -and -not $_.Source.StartsWith($shimRoot, [System.StringComparison]::OrdinalIgnoreCase) } |
    Select-Object -First 1
  if ($pnpmCmd) {
    return @{ FilePath = $pnpmCmd.Source; Prefix = @() }
  }

  $pnpmAny = @(Get-Command pnpm -ErrorAction SilentlyContinue) |
    Where-Object { $_.Source -and -not $_.Source.StartsWith($shimRoot, [System.StringComparison]::OrdinalIgnoreCase) } |
    Select-Object -First 1
  if ($pnpmAny -and $pnpmAny.Source) {
    return @{ FilePath = $pnpmAny.Source; Prefix = @() }
  }

  throw "pnpm is required. Install Node.js with Corepack enabled, or install pnpm globally."
}

function Invoke-PnpmCommand([hashtable]$Runner, [string[]]$Arguments, [string]$StepName) {
  $allArgs = @()
  $allArgs += $Runner.Prefix
  $allArgs += $Arguments
  Invoke-ExternalCommand -FilePath $Runner.FilePath -Arguments $allArgs -StepName $StepName
}

function Ensure-WindowsPnpmShim([hashtable]$Runner) {
  $shimRoot = Get-WindowsPnpmShimRoot

  New-Item -ItemType Directory -Force -Path $shimRoot | Out-Null
  $shimPath = Join-Path $shimRoot 'pnpm.cmd'

  $runnerPath = $Runner.FilePath
  $runnerPrefix = ($Runner.Prefix -join ' ')
  $shimCommand = if ([string]::IsNullOrWhiteSpace($runnerPrefix)) {
    "`"$runnerPath`" %*"
  } else {
    "`"$runnerPath`" $runnerPrefix %*"
  }

  Set-Content -Path $shimPath -Value "@echo off`r`n$shimCommand`r`n" -Encoding ASCII

  $pathEntries = $env:Path -split ';'
  if (-not ($pathEntries | Where-Object { $_ -ieq $shimRoot })) {
    $env:Path = "$shimRoot;$($env:Path)"
  }

  return $shimPath
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

if (-not (Test-Path $ReClawRepo)) {
  throw "ReClaw repo not found: $ReClawRepo"
}
if ($OpenClawRepo -match '\s') {
  throw "OpenClaw repo path contains spaces: $OpenClawRepo. Use a path without spaces (default: $(Get-DefaultOpenClawRepoPath)) or run in WSL2 as recommended by OpenClaw docs for Windows source workflows."
}

$pnpmRunner = Resolve-PnpmRunner
$pnpmShim = Ensure-WindowsPnpmShim -Runner $pnpmRunner
Log "Prepared pnpm shim for Windows compatibility: $pnpmShim"

Log "Ensuring OpenClaw repo exists"
if (-not (Test-Path (Join-Path $OpenClawRepo '.git'))) {
  if (Test-Path $OpenClawRepo) {
    Log "Found non-git OpenClaw folder. Recreating clean clone target."
    Remove-Item -Recurse -Force $OpenClawRepo -ErrorAction SilentlyContinue
  }
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OpenClawRepo) | Out-Null
  Invoke-ExternalCommand -FilePath 'git' -Arguments @('clone', $OpenClawRepoUrl, $OpenClawRepo) -StepName 'Clone OpenClaw repo'
}

Push-Location $OpenClawRepo
try {
  Log "OpenClaw docs flow: pnpm install -> pnpm ui:build -> pnpm build"
  Invoke-PnpmCommand -Runner $pnpmRunner -Arguments @('install') -StepName 'Install dependencies'
  Invoke-PnpmCommand -Runner $pnpmRunner -Arguments @('ui:build') -StepName 'Build Control UI assets'
  try {
    Invoke-PnpmCommand -Runner $pnpmRunner -Arguments @('build') -StepName 'Build OpenClaw'
  } catch {
    Log "pnpm build failed on native Windows. OpenClaw docs recommend WSL2 for source workflows."
    Log "Continuing with source runtime via pnpm openclaw commands."
  }

  $controlUiIndex = Join-Path $OpenClawRepo 'dist\control-ui\index.html'
  if (-not (Test-Path $controlUiIndex)) {
    throw "Control UI assets are missing after build: $controlUiIndex"
  }
} finally {
  Pop-Location
}

Log "Forcing gateway.mode=local when missing"
$modeFixScript = @"
const fs = require('fs');
const path = require('path');
const home = process.env.OPENCLAW_HOME || path.join(process.env.USERPROFILE || process.env.HOME, '.openclaw');
const cfgPath = path.join(home, 'openclaw.json');
if (!fs.existsSync(cfgPath)) process.exit(0);
const cfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
cfg.gateway = cfg.gateway || {};
if (!cfg.gateway.mode) {
  cfg.gateway.mode = 'local';
  fs.writeFileSync(cfgPath, JSON.stringify(cfg, null, 2));
  console.log('[fresh-install-win] Set gateway.mode=local');
}
"@
Invoke-ExternalCommand -FilePath 'node' -Arguments @('-e', $modeFixScript) -StepName 'Set gateway mode'

Push-Location $OpenClawRepo
try {
  Log "Running doctor fix (best effort)"
  $doctorArgs = @()
  $doctorArgs += $pnpmRunner.Prefix
  $doctorArgs += @('openclaw', 'doctor', '--fix', '--non-interactive')
  & $pnpmRunner.FilePath @doctorArgs
  if ($LASTEXITCODE -ne 0) {
    Log "Doctor fix returned non-zero. Continuing."
  }

  Log "Stopping old local gateway listeners"
  try {
    $oldGateway = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
      ($_.Name -match '^node(\.exe)?$') -and ($_.CommandLine -match 'openclaw\.mjs\s+gateway')
    }
    foreach ($proc in $oldGateway) {
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

  $openclawHome = if ($env:OPENCLAW_HOME) { $env:OPENCLAW_HOME } else { Join-Path $env:USERPROFILE '.openclaw' }
  New-Item -ItemType Directory -Force -Path $openclawHome | Out-Null
  $gatewayOutLog = Join-Path $openclawHome 'gateway-local.log'
  $gatewayErrLog = Join-Path $openclawHome 'gateway-local.err.log'

  Log "Starting local gateway"
  $gatewayArgs = @()
  $gatewayArgs += $pnpmRunner.Prefix
  $gatewayArgs += @('openclaw', 'gateway', '--port', '18789')
  Start-Process -FilePath $pnpmRunner.FilePath -ArgumentList $gatewayArgs -WorkingDirectory $OpenClawRepo -RedirectStandardOutput $gatewayOutLog -RedirectStandardError $gatewayErrLog -WindowStyle Hidden | Out-Null

  $healthy = $false
  for ($i = 0; $i -lt 25; $i++) {
    Start-Sleep -Seconds 1
    try {
      $response = Invoke-WebRequest -Uri 'http://127.0.0.1:18789/healthz' -UseBasicParsing -TimeoutSec 3
      if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
        $healthy = $true
        break
      }
    } catch {}
  }

  if (-not $healthy) {
    Write-Host "Gateway health check failed. Last logs:" -ForegroundColor Red
    if (Test-Path $gatewayOutLog) {
      Get-Content -Path $gatewayOutLog -Tail 80
    }
    if (Test-Path $gatewayErrLog) {
      Get-Content -Path $gatewayErrLog -Tail 80
    }
    throw "Gateway failed health check on http://127.0.0.1:18789/healthz"
  }

  Log "Fresh install complete"
  Log "Gateway: http://127.0.0.1:18789"
  Log "Health: ok"
} finally {
  Pop-Location
}
