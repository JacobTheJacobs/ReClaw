param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [switch]$All,
    [switch]$Run = $true
)

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$dist = Join-Path $root "dist"
$cliProject = Join-Path $root "ReClaw.Cli/ReClaw.Cli.csproj"
$desktopProject = Join-Path $root "ReClaw.Desktop/ReClaw.Desktop.csproj"

$rids = @($Rid)
if ($All) {
    $rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
}

foreach ($rid in $rids) {
    $cliOut = Join-Path $dist "cli/$rid"
    $desktopOut = Join-Path $dist "desktop/$rid"

    dotnet publish $cliProject -c $Configuration -r $rid --self-contained true -o $cliOut
    if ($LASTEXITCODE -ne 0) {
        throw "CLI publish failed for $rid."
    }
    dotnet publish $desktopProject -c $Configuration -r $rid --self-contained true -o $desktopOut
    if ($LASTEXITCODE -ne 0) {
        throw "Desktop publish failed for $rid."
    }

    if (-not $All -and $Run -and ($rid -like "win-*")) {
        $exe = Join-Path $desktopOut "ReClaw.Desktop.exe"
        if (Test-Path $exe) {
            Start-Process -FilePath $exe | Out-Null
        }
    }
}
