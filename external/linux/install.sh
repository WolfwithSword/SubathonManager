#!/usr/bin/env bash
# Installs native Linux handlers that forward subathonmanager:// links
# Run SubathonManager once inside the prefix first so the wine registry keys exist.
# Somewhat untested
set -euo pipefail

PREFIX="${1:-${WINEPREFIX:-$HOME/.wine}}"
WINE_BIN="${WINE_BIN:-wine}"

if [ ! -d "$PREFIX" ]; then
    echo "wine prefix not found: $PREFIX" >&2
    echo "usage: $0 [/path/to/wineprefix]   (or set WINEPREFIX)" >&2
    exit 1
fi

if ! command -v "$WINE_BIN" >/dev/null 2>&1 && [ ! -x "$WINE_BIN" ]; then
    echo "wine binary not found: $WINE_BIN (set WINE_BIN to override, e.g. for proton)" >&2
    exit 1
fi

APPS="$HOME/.local/share/applications"
MIME="$HOME/.local/share/mime"
mkdir -p "$APPS" "$MIME/packages"

cat > "$APPS/subathonmanager-url.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Subathon Manager (URL handler)
Exec=env WINEPREFIX="$PREFIX" "$WINE_BIN" start %u
MimeType=x-scheme-handler/subathonmanager;
NoDisplay=true
Terminal=false
EOF

cat > "$APPS/subathonmanager-smo.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Subathon Manager (.smo import)
Exec=env WINEPREFIX="$PREFIX" "$WINE_BIN" start /unix %f
MimeType=application/x-subathonmanager-overlay;
NoDisplay=true
Terminal=false
EOF

cp "$(dirname "$0")/subathonmanager-smo.xml" "$MIME/packages/subathonmanager-smo.xml"

update-mime-database "$MIME" >/dev/null 2>&1 || true
update-desktop-database "$APPS" >/dev/null 2>&1 || true
xdg-mime default subathonmanager-url.desktop x-scheme-handler/subathonmanager
xdg-mime default subathonmanager-smo.desktop application/x-subathonmanager-overlay

echo "installed:"
echo "  $APPS/subathonmanager-url.desktop"
echo "  $APPS/subathonmanager-smo.desktop"
echo "  $MIME/packages/subathonmanager-smo.xml"
echo ""
echo "test with: xdg-open \"subathonmanager://test\""
echo "reminder: SubathonManager must have been run once in this prefix so its registry keys exist."
