# WorkParcel 0.2.0 Beta

WorkParcel 0.2.0 Beta is a public beta for Windows 10/11 x64. It saves a selected work setup as a local parcel and lets you reopen supported items later.

## What WorkParcel does

- Create reusable parcels with a name, description, and selected items.
- Capture open application windows, files, folders, applications, web links, notes, and eligible Chrome or Edge tabs.
- Create a parcel while leaving the selected content open.
- Pack Away a chosen selection and optionally request a normal close for selected supported windows or tabs.
- Open selected parcel items later without touching unrelated resources.
- Edit or remove WorkParcel records without deleting the external files, folders, applications, or links.
- Track Today tasks, archive and restore parcels, check availability, and create local SQLite backups.
- Use System, Light, and Dark themes.

## Download and installation

Download the public beta from the [GitHub Releases page](https://github.com/arpitaa31/WorkParcel/releases). The installer is named `WorkParcel-Setup-0.2.0-beta.1.exe`.

1. Download the installer and verify its SHA-256 value against `SHA256SUMS.txt`.
2. Run the installer. It targets Windows 10/11 x64, installs per user under `%LOCALAPPDATA%\Programs\WorkParcel`, creates a Start-menu shortcut, and offers an optional desktop shortcut.
3. Launch WorkParcel from Start, Search, or the selected desktop shortcut. No source repository, VS Code, .NET SDK, or `dotnet run` is required.

The installer carries the .NET runtime and Windows App SDK dependencies. It does not start WorkParcel with Windows and does not require administrator rights under the normal per-user install.

## Browser extension

The browser extension is required only for browser-tab integration. It is not published in the Chrome Web Store or Microsoft Edge Add-ons. The separate download `WorkParcel-Browser-Extension-0.2.0-beta.1.zip` must be loaded as an unpacked extension, and the exact extension ID must be registered for the local native host. Browser-internal and private tabs are excluded. Files, folders, applications, open windows, manual web links, notes, parcels, Today, Archive, and Restore work without the extension.

See `BROWSER-EXTENSION-SETUP.md` for the manual Chrome/Edge setup. Uninstalling WorkParcel removes only its own files and current-user registration; it does not remove the browser or modify browser security settings.

## Local-data safety

Parcel data stays on the user's computer at `%LOCALAPPDATA%\WorkParcel\Data\workparcel.db`. Logs and cache are kept under the same WorkParcel LocalAppData folder. The installer contains no developer database, test parcels, browser profile, secrets, or source-repository files. Files and folders are linked by path; they are not copied, moved, or deleted. Ordinary uninstall preserves the WorkParcel data folder so a reinstall can find saved parcels.

## Known beta limitations

- The target package is Windows 10/11 x64.
- Browser-tab capture depends on the optional manually loaded extension and supported browser capabilities.
- Browser restore reopens supported URL and tab metadata; it does not restore page contents, login sessions, cookies, history, form data, or unsaved page state.
- Application reopening is based on saved item identity and availability. Window positions, monitor arrangements, and other exact desktop layouts are not restored.
- The installer may be unsigned. Windows SmartScreen can therefore show an unfamiliar-app warning. Review the release source, signature information, and checksum before deciding whether to run it; do not disable Windows security controls.

## Report bugs

Report reproducible beta issues through [GitHub Issues](https://github.com/arpitaa31/WorkParcel/issues). Do not attach private databases, parcel contents, browser profiles, credentials, tokens, or logs containing personal paths.
