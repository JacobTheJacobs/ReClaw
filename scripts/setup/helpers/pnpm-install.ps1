Write-Output "[helpers\pnpm-install.ps1] Attempt to enable corepack and prepare pnpm (non-destructive)"
if (Get-Command corepack -ErrorAction SilentlyContinue) {
  try { corepack enable; corepack prepare pnpm@latest --activate; Write-Output "corepack prepared pnpm" } catch { Write-Output "corepack prepare failed: $_" }
} else {
  Write-Output "corepack not found. Ensure Node >=16.10 is installed before proceeding."
}
