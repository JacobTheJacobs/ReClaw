param([int]$Port)
if (-not $Port) { Write-Output "Usage: kill-port.ps1 -Port <number>"; exit 2 }
Write-Output "[helpers\kill-port.ps1] Attempting to free port $Port (non-destructive where possible)"
try {
  $netProc = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue
  if ($netProc) {
    $pids = $netProc | Select-Object -ExpandProperty OwningProcess -Unique
    foreach ($pid in $pids) {
      try { Stop-Process -Id $pid -Force -ErrorAction Stop; Write-Output "Stopped PID $pid" } catch { Write-Output "Could not stop PID $pid: $_" }
    }
  } else {
    Write-Output "No process found listening on port $Port"
  }
} catch {
  Write-Output "Get-NetTCPConnection not available or failed: $_"
}
