param(
    [string]$Rid = "win-x64",
    [switch]$LaunchDesktop
)

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$cliDir = Join-Path $root "dist/cli/$Rid"
$desktopDir = Join-Path $root "dist/desktop/$Rid"
$cliExe = Join-Path $cliDir "ReClaw.Cli.exe"
$desktopExe = Join-Path $desktopDir "ReClaw.Desktop.exe"

if (!(Test-Path $cliExe)) {
    throw "CLI executable not found at $cliExe. Run publish first."
}

& $cliExe --help | Out-Null
& $cliExe version | Out-Null
& $cliExe status | Out-Null
& $cliExe action-list | Out-Null
& $cliExe backup verify --help | Out-Null
& $cliExe restore --help | Out-Null
& $cliExe doctor --help | Out-Null
& $cliExe fix --help | Out-Null
& $cliExe recover --help | Out-Null
& $cliExe rollback --help | Out-Null
& $cliExe reset --help | Out-Null

$smokeRoot = Join-Path $env:TEMP ("reclaw-smoke-" + [Guid]::NewGuid().ToString("N"))
$src = Join-Path $smokeRoot "src"
$dest = Join-Path $smokeRoot "dest"
New-Item -ItemType Directory -Path $src | Out-Null
Set-Content -Path (Join-Path $src "sample.txt") -Value "smoke"
$archive = Join-Path $smokeRoot "sample.tar.gz"

& $cliExe backup create --source $src --out $archive | Out-Null
if ($LASTEXITCODE -ne 0) { throw "backup create failed" }

& $cliExe restore --preview --snapshot $archive --dest $dest | Out-Null
if ($LASTEXITCODE -ne 0) { throw "restore preview failed" }

& $cliExe rollback --preview --snapshot $archive --dest $dest | Out-Null
if ($LASTEXITCODE -ne 0) { throw "rollback preview failed" }

& $cliExe reset --preview --reset-mode preserve-backups | Out-Null
if ($LASTEXITCODE -ne 0) { throw "reset preview failed" }

& $cliExe reset --reset-mode preserve-backups | Out-Null
if ($LASTEXITCODE -eq 0) { throw "reset confirmation gate did not block" }

if ($LaunchDesktop -and (Test-Path $desktopExe)) {
    $proc = Start-Process $desktopExe -PassThru
    Start-Sleep -Seconds 3
    Stop-Process -Id $proc.Id
}
