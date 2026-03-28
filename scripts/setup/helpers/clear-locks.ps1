Write-Output "[helpers\clear-locks.ps1] Renaming install.lock files (safe, non-destructive)"
$candidates = @(Join-Path $PSScriptRoot '..\..\install.lock' | Resolve-Path -ErrorAction SilentlyContinue,
                Join-Path $PSScriptRoot '..\..\openclaw\install.lock' | Resolve-Path -ErrorAction SilentlyContinue)
foreach ($p in $candidates) {
  if ($null -ne $p) {
    $bak = "$($p.Path).bak"
    Write-Output "Renaming $($p.Path) -> $bak"
    try { Rename-Item -Path $p.Path -NewName (Split-Path $bak -Leaf) -ErrorAction Stop } catch { Write-Output "Could not rename: $_" }
  } else {
    Write-Output "No lock found in candidate path"
  }
}
