# WorkParcel

### Save the work. Open it when you return.

WorkParcel saves a focused work setup as a local parcel so you can return to it later. Parcels can contain files, folders, applications, open application windows, web links, supported browser tabs, and notes.

## Download

WorkParcel 0.2.0 Beta is a public beta for Windows 10/11 x64. Download the installer and release checksums from the [GitHub Releases page](https://github.com/arpitaa31/WorkParcel/releases).

The beta installer includes the .NET runtime and Windows App SDK dependencies, so the installed app launches without VS Code, the .NET SDK, or `dotnet run`.

## How it works

1. Create a parcel and give it a name.
2. Select the files, folders, applications, open windows, links, browser tabs, and notes that belong in it.
3. Create the parcel. The selected content stays open and is not moved or copied.
4. Open the parcel later and choose which saved items to launch. Pack Away can save the current selection and, only when requested, ask selected supported applications or browser tabs to close normally.

## Main features

- Select open applications and windows, files, folders, applications, links, browser tabs, and notes.
- Reopen supported saved items without changing unrelated resources.
- Check availability, re-link missing files or folders, and accept changed file versions.
- Track Today tasks, archive parcels, and restore them from Archive.
- Use local SQLite storage, backups, and System, Light, or Dark themes.

## Safety and privacy

WorkParcel keeps data on the user's computer. It does not move, copy, or delete real files or folders. Removing an item removes only its WorkParcel record. Save and Close requests normal close messages; it does not force-kill applications or bypass unsaved-work prompts. WorkParcel does not read browser history, passwords, cookies, page contents, or keystrokes.

## Browser tabs

The optional Chrome/Edge browser extension is required only for browser-tab integration. It lets you choose eligible non-private HTTP/HTTPS tabs during capture and reopen them later. The extension is not yet published in the browser stores and must be loaded manually. Files, folders, applications, open windows, links, notes, parcels, Today, Archive, and Restore work without it. Manual web links do not require the extension.

## Beta status and bug reports

This is a public beta. Report reproducible bugs, compatibility problems, and feature feedback through [GitHub Issues](https://github.com/arpitaa31/WorkParcel/issues). Do not include private parcel contents, database files, browser profiles, credentials, or tokens in an issue.

## Run from source

You need Windows and the .NET 10 SDK.

```powershell
git clone https://github.com/arpitaa31/WorkParcel.git
cd WorkParcel
dotnet restore .\WorkParcel.slnx
dotnet build .\WorkParcel.slnx
dotnet test .\WorkParcel.slnx
dotnet run --project .\src\WorkParcel.App\WorkParcel.App.csproj --no-restore -p:WindowsPackageType=None -p:EnableMsixTooling=false -p:EnableWinAppRunSupport=false
```

## Built with

- C# and .NET 10
- WinUI 3 and Windows App SDK
- SQLite
- Win32 APIs for application-window detection and safe close requests
- Chrome and Edge browser extension integration
