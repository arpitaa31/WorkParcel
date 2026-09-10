# WorkParcel

### Save the work. Open it when you return.

WorkParcel saves the apps, files, folders and web links connected to a task, lets you pack that setup away, and reopen it later.

## Download

WorkParcel 0.2.0 Beta 3 is a public beta for Windows 10/11 x64. Download the installer, portable build, and release checksums from the GitHub Releases page.

The installer and portable build include the .NET runtime and Windows App SDK dependencies, so WorkParcel launches without VS Code, the .NET SDK, or dotnet run.

## How it works

1. Create a parcel and give it a name.
2. Capture the open apps and windows, files and folders, and web links that belong to the task.
3. Create the parcel. Everything stays open and external files and folders are not moved or copied.
4. Open the parcel later and choose which saved items to launch. Apps use their saved executable; web links open in the Windows default browser.
5. Pack Away saves the selected records and can request a normal close for selected whole application windows.

Capture Current Setup is organized into OPEN APPS, FILES & FOLDERS, and WEB LINKS. A web-link entry accepts one HTTP or HTTPS URL per line. Blank lines are ignored, invalid lines are shown for correction, and duplicate URLs are not added. WorkParcel validates and stores the URL; it does not fetch the page.

## Main features

- Save and reopen apps, whole application windows, files, folders, web links, and notes.
- Add, edit, remove, and relink saved records without changing the original resources.
- Check availability and accept changed file versions.
- Pack away only the whole windows you select. WorkParcel never closes an individual browser tab or uses saved links to close a browser.
- Track Today tasks, archive parcels, restore them from Archive, create local SQLite backups, and use System, Light, or Dark themes.

If you select a Chrome or Edge window in OPEN APPS, Pack Away may close that entire selected window, including its tabs. WorkParcel does not claim to preserve browser sessions, tab history, tab groups, page contents, or exact window positions.

## Safety and privacy

WorkParcel keeps data on the user's computer. It does not move, copy, or delete real files or folders. Removing an item removes only its WorkParcel record. Close requests are normal Windows close messages; WorkParcel does not force-kill applications or bypass unsaved-work prompts. Web links are ordinary saved URLs and are opened only when you choose to open them.

## Beta status and bug reports

This is a public beta. Report reproducible bugs, compatibility problems, and feature feedback through GitHub Issues. Do not include private parcel contents, database files, credentials, tokens, or logs containing personal paths in an issue.

## Run from source

You need Windows and the .NET 10 SDK.

    dotnet restore .\WorkParcel.slnx
    dotnet build .\WorkParcel.slnx
    dotnet test .\WorkParcel.slnx
    dotnet run --project .\src\WorkParcel.App\WorkParcel.App.csproj --no-restore -p:WindowsPackageType=None -p:EnableMsixTooling=false -p:EnableWinAppRunSupport=false

## Built with

- C# and .NET 10
- WinUI 3 and Windows App SDK
- SQLite
- Win32 APIs for application-window detection and safe whole-window close requests
