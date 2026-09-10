# WorkParcel

### Save your work. Open it whenever you return.

I started making WorkParcel because reopening the same apps, files, folders and links every time I return to a project gets annoying.

WorkParcel lets you save everything connected to a task inside one parcel. Later, you can open that parcel and quickly get back to your work.

## Download

WorkParcel `v0.2.0-beta.3` is available for Windows 10/11 (x64).

Download the installer from the GitHub Releases page:

`WorkParcel-Setup-0.2.0-beta.3.exe`

The required .NET and Windows App SDK files are already included, so you do not need VS Code or the .NET SDK to use the app.

A portable ZIP is also available if you do not want to install it.

## How it works

1. Create a new parcel.
2. Select the apps, windows, files and folders connected to your work.
3. Add any useful web links.
4. Save the parcel. Your current setup will remain open.
5. Use **Pack Away** when you want to close the selected supported windows.
6. Use **Open Parcel** whenever you want to reopen the setup.

Web links open in your default browser. WorkParcel does not automatically save or close individual Chrome or Edge tabs.

## Features

- Create and manage different parcels
- Capture currently open apps and windows
- Add files, folders and web links
- Open saved parcel items again
- Pack away selected application windows
- Add a to-do list to a particular parcel
- Edit and remove individual saved items
- Archive parcels that are no longer needed
- Restore archived parcels
- Search through saved parcels
- System, Light and Dark themes
- Local SQLite storage

Removing an item from a parcel only removes its WorkParcel record. The original file, folder or application is never deleted.

## Privacy and safety

All WorkParcel data stays locally on your computer.

WorkParcel does not upload your parcels or copy your real files. It also uses normal Windows close requests instead of forcefully killing applications, so apps can still show their usual unsaved-work warning.

## Beta version

This is still a public beta, so there may be a few bugs or compatibility issues.

If you find a problem, please report it through GitHub Issues. Do not upload personal parcel data, database files, passwords, tokens or private file paths.

## Run from source

You need Windows and the .NET 10 SDK.

```powershell
dotnet restore .\WorkParcel.slnx
dotnet build .\WorkParcel.slnx
dotnet test .\WorkParcel.slnx
dotnet run --project .\src\WorkParcel.App\WorkParcel.App.csproj --no-restore -p:WindowsPackageType=None -p:EnableMsixTooling=false -p:EnableWinAppRunSupport=false
