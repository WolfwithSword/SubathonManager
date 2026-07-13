# Linux (Wine/Proton) support files

SubathonManager might work on Linux under Wine/Proton. These files help fill in some gaps.

## Install

1. Run SubathonManager at least once inside your wine prefix to write its registry keys on boot.
2. Run the installer:

   ./install.sh /path/to/wineprefix

   - No argument: uses $WINEPREFIX, else ~/.wine
   - Custom wine binary (proton, wine-staging, etc):
     WINE_BIN=/path/to/wine ./install.sh /path/to/prefix
3. Test: xdg-open "subathonmanager://test" should focus the running app.

Everything is installed user-level, no root needed:

- ~/.local/share/applications/subathonmanager-url.desktop  - handles subathonmanager:// links
- ~/.local/share/applications/subathonmanager-smo.desktop  - opens .smo files via wine
- ~/.local/share/mime/packages/subathonmanager-smo.xml     - defines the .smo mime type

## Remove

Delete the three files above, then:

    update-mime-database ~/.local/share/mime
    update-desktop-database ~/.local/share/applications

## Notes

- The WebView2 overlay preview is not available under wine/proton. The app detects this and fallsback to opening a browser for the editor preview.
- If you move the app to a different prefix, re-run install.sh with the new path.
- Secrets: under wine the app uses DPAPI like on Windows. Native (non-wine) builds use an AES file store (data/secure_store.bin + data/secure_store.key) instead, in case I can ever get this native in the future.
