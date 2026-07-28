#!/usr/bin/env bash

# Usage: make-app-bundle.sh <publish_dir> <out_dir> <info_plist> <icon_png> [exe_name]
set -euo pipefail

PUBLISH_DIR="$1"
OUT_DIR="$2"
PLIST="$3"
ICON_PNG="${4:-}"
EXE_NAME="${5:-SubathonManager}"
VERSION="${6:-}"

APP="$OUT_DIR/SubathonManager.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
cp "$PLIST" "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/$EXE_NAME"

if [ -n "$VERSION" ]; then
    SHORT_VERSION="$(echo "$VERSION" | cut -d. -f1-3)"
    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $SHORT_VERSION" "$APP/Contents/Info.plist" || true
    /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$APP/Contents/Info.plist" || true
fi

if [ -n "$ICON_PNG" ] && [ -f "$ICON_PNG" ] \
   && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
    ICONSET="$(mktemp -d)/icon.iconset"
    mkdir -p "$ICONSET"
    gen() { sips -z "$1" "$1" "$ICON_PNG" --out "$ICONSET/$2" >/dev/null; }
    gen 16   icon_16x16.png
    gen 32   icon_16x16@2x.png
    gen 32   icon_32x32.png
    gen 64   icon_32x32@2x.png
    gen 128  icon_128x128.png
    gen 256  icon_128x128@2x.png
    gen 256  icon_256x256.png
    gen 512  icon_256x256@2x.png
    gen 512  icon_512x512.png
    gen 1024 icon_512x512@2x.png
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/icon.icns" || true
fi

if command -v codesign >/dev/null 2>&1; then
    codesign --force --deep --sign - "$APP" || true
fi

echo "built $APP"
