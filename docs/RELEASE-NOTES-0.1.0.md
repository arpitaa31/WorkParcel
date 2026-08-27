# WorkParcel 0.1.0

WorkParcel is a local, terminal-inspired workspace for saving a setup and returning to it later. It keeps parcel data in SQLite on the current Windows user account.

## Included

- Create focused parcels with a name and description.
- Save open application windows when Windows exposes their executable and window metadata.
- Add files, folders, applications, web links, and notes to a parcel.
- Open a parcel to launch or open supported saved items.
- Pack Away a parcel and keep a lightweight history of its state.
- Track Today tasks.
- Archive parcels and restore them from Archive.
- Use local SQLite persistence with automatic schema initialization and backups.
- Use a compact terminal-inspired interface with System, Light, and Dark theme options.
- Optionally capture supported Chrome and Edge tab metadata through the separately bundled Chromium extension.

## Browser extension status

The browser bridge is included as `WorkParcel-Browser-Extension-0.1.0.zip` and as an unpacked folder in the installer and portable build. It is not published in the Chrome Web Store or Microsoft Edge Add-ons. Loading it is user-driven, and the exact extension ID must be supplied when registering the current-user native host. Chrome and Edge can be registered independently.

The browser bridge stores metadata such as URL, title, domain, browser family, window/group placement, pinned/active state, and browser-provided favicon references. Private/incognito tabs and browser-internal pages are excluded. WorkParcel does not claim to restore form contents, login sessions, cookies, history, scroll position, unsaved page data, or complete browser state.

## Privacy and local data

The primary database is `%LOCALAPPDATA%\WorkParcel\Data\workparcel.db`. Logs are in `%LOCALAPPDATA%\WorkParcel\Logs`, browser-host diagnostics are in `%LOCALAPPDATA%\WorkParcel\BrowserHost`, and icon cache data is in `%LOCALAPPDATA%\WorkParcel\Cache`. The installer does not package the developer database. Reinstall and ordinary uninstall preserve the local WorkParcel data folder.

## Installation

Run `WorkParcel-Setup-0.1.0.exe`. The per-user installer defaults to `%LOCALAPPDATA%\Programs\WorkParcel`, creates a Start Menu entry, and offers an optional Desktop shortcut. After setup, launch WorkParcel from Start, Search, or the selected Desktop shortcut. No .NET SDK is required.

Windows SmartScreen may warn because this first release is unsigned. Inspect the warning and publisher/signature information before deciding whether to trust the file. Code signing is a future release improvement; do not disable Windows security controls.

## Portable build

Extract `WorkParcel-Portable-0.1.0-win-x64.zip`, open the extracted folder, and double-click `WorkParcel.exe`. Keep its supporting DLLs and folders together. The portable build also uses Local AppData for user data and does not require a separate .NET runtime.

## Known limitations

- Windows x64 is the release target; other architectures are not packaged here.
- The optional extension is unpacked/user-installed and is not store-published.
- Browser capture excludes private and unsupported internal pages.
- Browser restore is limited to supported URL/tab metadata and browser capabilities; it is not exact application-state restoration.
- The first installer is unsigned and may produce a SmartScreen warning.
