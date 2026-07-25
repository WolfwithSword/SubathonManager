#!/usr/bin/env bash
# Registers Linux binary as the handler for subathonmanager:// links
# and .smo overlay files. The app should self-register this already on launch, however, this is to manually set it up if desired or remove it.
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
DESKTOP="$APPS/subathonmanager.desktop"
MIME_XML="$MIME/packages/subathonmanager-overlay.xml"
SCHEME="x-scheme-handler/subathonmanager"
OVERLAY="application/x-subathonmanager-overlay"

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$DESKTOP" "$MIME_XML"
    update-mime-database "$MIME" >/dev/null 2>&1 || true
    update-desktop-database "$APPS" >/dev/null 2>&1 || true
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

ICON_LINE=""
[ -f "$APP_DIR/Assets/icon.png" ] && ICON_LINE="Icon=$APP_DIR/Assets/icon.png"

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
MimeType=$OVERLAY;$SCHEME;
EOF

cat > "$MIME_XML" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="$OVERLAY">
    <comment>Subathon Manager Overlay</comment>
    <glob pattern="*.smo"/>
  </mime-type>
</mime-info>
EOF

update-mime-database "$MIME" >/dev/null 2>&1 || true
update-desktop-database "$APPS" >/dev/null 2>&1 || true
xdg-mime default subathonmanager.desktop "$SCHEME"
xdg-mime default subathonmanager.desktop "$OVERLAY"

echo "installed:"
echo "  $DESKTOP  ->  $BIN"
echo "  $MIME_XML"
echo ""
echo "test with: xdg-open \"subathonmanager://test\"   (should focus the running app)"
