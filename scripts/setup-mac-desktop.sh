#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
DIST_DIR="$ROOT_DIR/desktop-ui/dist"
ARCH="$(uname -m)"

case "$ARCH" in
  arm64)
    BUILD_ARCH="--arm64"
    APP_DIR="$DIST_DIR/mac-arm64"
    ;;
  x86_64)
    BUILD_ARCH="--x64"
    APP_DIR="$DIST_DIR/mac"
    ;;
  *)
    echo "Unsupported macOS architecture: $ARCH"
    exit 1
    ;;
esac

echo "[ReClaw] Building desktop app for macOS ($ARCH)..."
cd "$ROOT_DIR"
npm run desktop:pack:mac -- "$BUILD_ARCH"

echo "[ReClaw] Build complete. Looking for DMG artifact..."
DMG_FILE="$(ls -t "$DIST_DIR"/*.dmg 2>/dev/null | head -n 1 || true)"
if [[ -z "$DMG_FILE" ]]; then
  echo "No DMG was produced. Check build logs under $DIST_DIR"
  exit 1
fi

echo "[ReClaw] Installer ready: $DMG_FILE"

echo "[ReClaw] Looking for app bundle..."
APP_BUNDLE="$APP_DIR/ReClaw.app"
if [[ -d "$APP_BUNDLE" ]]; then
  echo "[ReClaw] Launching built app once for verification..."
  open "$APP_BUNDLE"
  echo "[ReClaw] App launch command sent: $APP_BUNDLE"
else
  echo "App bundle not found at $APP_BUNDLE (this can happen with builder layout differences)."
fi

echo ""
echo "Next steps:"
echo "1) Open installer DMG: open \"$DMG_FILE\""
echo "2) Drag ReClaw.app into Applications"
echo "3) If macOS blocks first launch, run: xattr -dr com.apple.quarantine /Applications/ReClaw.app"
