#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${1:-Release}"
RID="${2:-win-x64}"
ALL="${3:-}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST="$ROOT/dist"
CLI_PROJECT="$ROOT/ReClaw.Cli/ReClaw.Cli.csproj"
DESKTOP_PROJECT="$ROOT/ReClaw.Desktop/ReClaw.Desktop.csproj"

if [[ "$ALL" == "--all" ]]; then
  RIDS=("win-x64" "win-arm64" "linux-x64" "linux-arm64" "osx-x64" "osx-arm64")
else
  RIDS=("$RID")
fi

for rid in "${RIDS[@]}"; do
  CLI_OUT="$DIST/cli/$rid"
  DESKTOP_OUT="$DIST/desktop/$rid"

  dotnet publish "$CLI_PROJECT" -c "$CONFIGURATION" -r "$rid" --self-contained true -o "$CLI_OUT"
  dotnet publish "$DESKTOP_PROJECT" -c "$CONFIGURATION" -r "$rid" --self-contained true -o "$DESKTOP_OUT"
done
