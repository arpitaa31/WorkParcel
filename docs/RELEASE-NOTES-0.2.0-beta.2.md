# WorkParcel 0.2.0 Beta 2

WorkParcel 0.2.0 Beta 2 is a Windows 10/11 x64 public beta for saving a focused work setup as a local parcel and reopening it later.

## What changed since Beta 1

- Repaired the Settings browser-tab actions and added a guided Chrome/Edge setup dialog.
- Added working browser-extension page, installed extension-folder, connection-check, diagnostics, setup-help, and confirmation actions.
- Replaced the unreliable browser-card expander with an accessible, independent Advanced Details control.
- Added truthful connection states: Connected, Extension not detected, Desktop connection unavailable, Browser unavailable, and Connection failed.
- Added visible busy, success, and failure feedback and guarded asynchronous Settings actions against repeated clicks.
- Kept browser-tab privacy preferences persistent and documented the data boundary clearly.

## Browser tabs

The optional browser extension is loaded manually from the installed `browser-extension` folder. Chrome and Edge integration use the exact extension ID shown by each browser; WorkParcel never bypasses browser security or registers a wildcard origin. Browser-tab capture stores the title and URL only. Passwords, cookies, page contents, and keystrokes are not captured, and private browsing tabs are excluded.

The installer and portable build include the extension files, native host files, setup script, icons, and user-facing documentation. The browser extension is not published in the Chrome Web Store or Microsoft Edge Add-ons.

## Installation

The installer is `WorkParcel-Setup-0.2.0-beta.2.exe`. It is a per-user self-contained x64 install and normally uses `%LOCALAPPDATA%\Programs\WorkParcel`. Verify `SHA256SUMS.txt` before running it. The portable package is `WorkParcel-Portable-0.2.0-beta.2-win-x64.zip`.

For browser setup, use Settings -> BROWSER TABS or read `BROWSER-EXTENSION-SETUP.md`. Browser-tab integration remains optional; parcels containing files, folders, applications, links, notes, and other supported items work without it.

## Known limitations

- The extension must be loaded unpacked and registered for the current Windows user.
- Browser restore reopens supported URL and tab metadata; it does not restore page contents, login sessions, cookies, history, form data, or unsaved page state.
- The installer may be unsigned and Windows SmartScreen may show an unfamiliar-app warning.
