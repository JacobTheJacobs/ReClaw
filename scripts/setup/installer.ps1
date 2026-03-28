<#
  Windows installer equivalent for unattended setup
  Non-destructive; supports mock mode via OPENCLAW_ASSUME_OFFLINE=1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent -Path $MyInvocation.MyCommand.Definition
Push-Location $root\..\..

function Log([string]$m){ Write-Output "[installer] $m" }
function Err([string]$m){ Write-Error "[installer][ERROR] $m" }

$env:OPENCLAW_INSTALL_TIMEOUT = $env:OPENCLAW_INSTALL_TIMEOUT -or 300
$env:OPENCLAW_RETRY = $env:OPENCLAW_RETRY -or 3
$assumeOffline = $env:OPENCLAW_ASSUME_OFFLINE -eq '1'

function Run-Or-Stub([string]$cmd){
  if ($assumeOffline) { Log "MOCK MODE: would run: $cmd"; return }
  Log "running: $cmd"
  iex $cmd
}

function Ensure-CorepackAndPnpm {
  Log "Ensuring corepack and pnpm"
  if (Get-Command corepack -ErrorAction SilentlyContinue) {
    if (-not $assumeOffline) { corepack enable }
    if (-not $assumeOffline) { corepack prepare pnpm@latest --activate }
  } else {
    Err "corepack not found. Ensure Node 16.10+ is installed."
  }
  if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
    Log "pnpm not present; invoking helper"
    & "$PSScriptRoot\helpers\pnpm-install.ps1"
  }
}

try {
  Log "Starting unattended Windows setup"
  Ensure-CorepackAndPnpm

  Run-Or-Stub "pnpm install --shamefully-hoist"
  Run-Or-Stub "pnpm openclaw gateway install"
  Run-Or-Stub "pnpm openclaw gateway start"

  $tries = 0
  while ($tries -lt [int]$env:OPENCLAW_RETRY) {
    $tries++
    Log "Onboarding attempt $tries/$($env:OPENCLAW_RETRY)"
    if ($assumeOffline) {
      Log "MOCK MODE: skipping real onboarding"
      $success = $true
    } else {
      try {
        & pnpm openclaw installer/onboard/run -- --accept-license --auto
        $success = $true
      } catch {
        $success = $false
      }
    }
    if ($success) {
      Log "Onboarding successful; writing marker"
      New-Item -ItemType Directory -Path openclaw -Force | Out-Null
      (Get-Date).ToUniversalTime().ToString('s') + 'Z' | Out-File -Encoding utf8 openclaw\.onboarded
      Pop-Location
      exit 0
    } else {
      Err "Onboard failed on attempt $tries"
      & "$PSScriptRoot\helpers\fix-gateway-mode.ps1" -ErrorAction SilentlyContinue
      & "$PSScriptRoot\helpers\clear-locks.ps1" -ErrorAction SilentlyContinue
      Start-Sleep -Seconds 2
    }
  }

  Err "All onboarding attempts failed"
  Pop-Location
  exit 2
} catch {
  Err $_.Exception.Message
  Pop-Location
  exit 3
}
