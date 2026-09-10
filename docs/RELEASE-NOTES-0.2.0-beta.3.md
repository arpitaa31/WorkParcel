# WorkParcel 0.2.0 Beta 3

WorkParcel 0.2.0 Beta 3 is a Windows 10/11 x64 public beta for saving a focused work setup as a local parcel and reopening it later.

## What changed

- Simplified WorkParcel around apps, whole application windows, files, folders, web links, and notes.
- Removed browser-internal capture, tab restore, tab close, native messaging, and browser add-on packaging.
- Added ordinary HTTP/HTTPS web links with one URL per line, optional names, validation, duplicate prevention, editing, removal, and SQLite persistence.
- Opening a parcel launches saved web links through the Windows default browser and continues when an individual launch fails.
- Pack Away operates only on selected whole application windows and sends normal close requests; it never closes individual tabs.
- Preserved existing parcel data and convert legacy browser-tab rows to ordinary web-link records during startup without recreating the database.
- Updated the installer, portable package, settings, capture flow, details flow, help text, and diagnostics to match the simplified behavior.

## Installation

The installer is WorkParcel-Setup-0.2.0-beta.3.exe. It is a per-user self-contained x64 install and normally uses %LOCALAPPDATA%\Programs\WorkParcel. Verify SHA256SUMS.txt before running it. The portable package is WorkParcel-Portable-0.2.0-beta.3-win-x64.zip.

The installer and portable build contain only the WorkParcel app and user-facing documentation. No source repository, developer database, logs, credentials, or browser profile is packaged.

## Web links and whole windows

Web links accept only HTTP and HTTPS URLs. WorkParcel trims each line, ignores blanks, reports invalid lines, preserves valid links, and does not fetch pages while saving them. Opening a link uses the Windows default browser.

Capture Current Setup lists open applications and whole windows, files and folders, and web links. If a selected Chrome or Edge window is packed away, Windows may close that entire window and all tabs in it. Saved web links never close a browser. WorkParcel does not restore browser sessions, tab history, tab groups, page contents, or exact desktop positions.

## Local-data safety

Parcel data stays on the user's computer at %LOCALAPPDATA%\WorkParcel\Data\workparcel.db. Logs and cache are kept under the same WorkParcel LocalAppData folder. Files and folders are linked by path; they are not copied, moved, or deleted. Ordinary uninstall preserves the WorkParcel data folder so a reinstall can find saved parcels.

## Known beta limitations

- The target package is Windows 10/11 x64.
- Application reopening is based on saved item identity and availability. Window positions, monitor arrangements, and other exact desktop layouts are not restored.
- The installer may be unsigned. Windows SmartScreen can therefore show an unfamiliar-app warning. Review the release source, signature information, and checksum before deciding whether to run it; do not disable Windows security controls.

## Report bugs

Report reproducible beta issues through GitHub Issues. Do not attach private databases, parcel contents, credentials, tokens, or logs containing personal paths.
