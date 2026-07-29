#!/usr/bin/env bash
# Registers Linux binary as the handler for subathonmanager:// links
# and .smo + .smw files
# The app should self-register this already on launch, however, this is to manually set it up if desired or remove it.
#
# Usage:
#   ./install.sh              register using the SubathonManager binary next to this script
#   ./install.sh /path/to/dir register using the binary in that directory
#   ./install.sh --uninstall  remove handlers
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
APPS="$DATA_HOME/applications"
MIME="$DATA_HOME/mime"
HICOLOR="$DATA_HOME/icons/hicolor"
DESKTOP="$APPS/subathonmanager.desktop"
MIME_XML="$MIME/packages/subathonmanager-overlay.xml"
SCHEME="x-scheme-handler/subathonmanager"
OVERLAY="application/x-subathonmanager-overlay"
WIDGET="application/x-subathonmanager-widget"
COLLECTION="application/x-subathonmanager-widget-collection"
ICON_NAME="subathonmanager"
ICON_SIZES="48 64 128 256"

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$DESKTOP" "$MIME_XML"
    for size in $ICON_SIZES; do
        rm -f "$HICOLOR/${size}x${size}/apps/$ICON_NAME.png"
    done
    update-mime-database "$MIME" >/dev/null 2>&1 || true
    update-desktop-database "$APPS" >/dev/null 2>&1 || true
    gtk-update-icon-cache -f -t "$HICOLOR" >/dev/null 2>&1 || true
    echo "removed SubathonManager handlers"
    exit 0
fi

APP_DIR="${1:-$SCRIPT_DIR}"
BIN="$APP_DIR/SubathonManager"
if [ ! -x "$BIN" ]; then
    echo "SubathonManager binary not found or not executable: $BIN" >&2
    echo "usage: $0 [/path/to/app/dir]   (defaults to the folder containing this script)" >&2
    exit 1
fi

mkdir -p "$APPS" "$MIME/packages"

ICON_INSTALLED=0
for size in $ICON_SIZES; do
    src="$APP_DIR/Assets/icon_${size}.png"
    [ -f "$src" ] || continue
    mkdir -p "$HICOLOR/${size}x${size}/apps"
    cp -f "$src" "$HICOLOR/${size}x${size}/apps/$ICON_NAME.png"
    ICON_INSTALLED=1
done

if [ "$ICON_INSTALLED" = "1" ]; then
    gtk-update-icon-cache -f -t "$HICOLOR" >/dev/null 2>&1 || true
    ICON_LINE="Icon=$ICON_NAME"
else
    ICON_LINE=""
    if [ -f "$APP_DIR/Assets/icon.png" ]; then
        ICON_LINE="Icon=$APP_DIR/Assets/icon.png"
    fi
fi

cat > "$DESKTOP" <<EOF
[Desktop Entry]
Type=Application
Name=Subathon Manager
Exec="$BIN" %U
$ICON_LINE
Terminal=false
NoDisplay=false
Categories=Utility;
StartupWMClass=SubathonManager
MimeType=$OVERLAY;$WIDGET;$COLLECTION;$SCHEME;
EOF

cat > "$MIME_XML" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="$OVERLAY">
    <comment>Subathon Manager Overlay</comment>
    <icon name="$ICON_NAME"/>
    <glob pattern="*.smo"/>
  </mime-type>
  <mime-type type="$WIDGET">
    <comment>Subathon Manager Widget</comment>
    <icon name="$ICON_NAME"/>
    <glob pattern="*.smw"/>
  </mime-type>
  <mime-type type="$COLLECTION">
    <comment>Subathon Manager Widget Collection</comment>
    <icon name="$ICON_NAME"/>
    <glob pattern="*.smwc"/>
  </mime-type>
</mime-info>
EOF

update-mime-database "$MIME" >/dev/null 2>&1 || true
update-desktop-database "$APPS" >/dev/null 2>&1 || true
xdg-mime default subathonmanager.desktop "$SCHEME"
xdg-mime default subathonmanager.desktop "$OVERLAY"
xdg-mime default subathonmanager.desktop "$WIDGET"
xdg-mime default subathonmanager.desktop "$COLLECTION"

echo "installed:"
echo "  $DESKTOP  ->  $BIN"
echo "  $MIME_XML"
echo ""
echo "test with: xdg-open \"subathonmanager://test\"   (should focus the running app)"
