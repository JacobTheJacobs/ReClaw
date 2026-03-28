Write-Output "[helpers\fix-gateway-mode.ps1] Non-destructive gateway-mode helper"
Write-Output "This helper will suggest actions and create a marker file."
$marker = Join-Path $PSScriptRoot '.markers'
New-Item -ItemType Directory -Path $marker -Force | Out-Null
(Get-Date).ToUniversalTime().ToString('s') + 'Z' | Out-File -Encoding utf8 (Join-Path $marker 'gateway-fix-suggested')
Write-Output "Marker written to $marker"
