==================================
 SubathonManager for macOS
==================================

SubathonManager is not signed with an Apple Developer ID,
so macOS will warn you the first time you open it and may refuse to launch it.

This is normal for small indie apps, but to fix it, you only need to 
do the following once.

SubathonManager for macOS is considered experimental and not the primary
target for support.

------------------------------------------------------------------------
 1) Make sure you have the right version for your Mac
------------------------------------------------------------------------
  - Apple Silicon (M1 / M2 / M3 / M4)  -> use the "osx-arm64" download
  - Intel Mac                          -> use the "osx-x64" download

  An arm64 build will NOT run on an Intel Mac. An x64 build on Apple
  Silicon needs Rosetta 2

------------------------------------------------------------------------
 2) Move the app out of the Downloads folder
------------------------------------------------------------------------
  Drag "SubathonManager.app" into your Applications folder (or anywhere
  outside Downloads)

------------------------------------------------------------------------
 3) Open it past the warning (pick the one for your macOS version)
------------------------------------------------------------------------

  macOS 15 and newer:
    1. Double-click SubathonManager.app  (it will be blocked)
    2. Open  System Settings  ->  Privacy & Security
    3. Scroll down until you'll see a message about SubathonManager being
       blocked. Click "Open Anyway".
    4. Confirm, and enter your password if asked.

  macOS 14 and older:
    1. Right-click (or Control-click) SubathonManager.app
    2. Choose "Open"
    3. In the dialog, click "Open" again.

  After you've done this once, it should open normally from now on.

------------------------------------------------------------------------
 Still broken? ("app is damaged" or nothing happens)? Terminal fix
------------------------------------------------------------------------
  Open the Terminal app and run these two commands, replacing the path
  with where the app actually is

    xattr -dr com.apple.quarantine "SubathonManager.app"
    codesign --force --deep --sign - "SubathonManager.app"

  Then open the app normally. The first command removes the download
  "quarantine" flag; the second re-applies a local signature so Apple
  Silicon will run it.