# WorkParcel

WorkParcel is a Windows app I made cuz I was tired of openin the same apps, files, folders and links again and again whenever I returned to a project.

Like while workin on smth, we usually have a whole setup open. Once everything is closed, finding all of it again is annoying. So WorkParcel lets ppl save that setup inside a parcel and open it again later.

## What can it do?

* Make separate parcels for different projects
* Select the apps and windows currently open
* Add files, folders and web links
* Open saved items again
* Pack away selected app windows
* Make a to-do list for a particular parcel
* Remove smth from a parcel without deleting the actual file
* Archive parcels and bring them back later
* Search through saved parcels
* Use Light, Dark or System theme

All the parcel data is saved locally on the computer using SQLite.

## How does it work?

1. Make a new parcel.
2. Select the apps or windows u wanna save.
3. Add any files, folders or useful links.
4. Save the parcel. Everything stays open at this point.
5. Click **Pack Away** whenever u wanna close the selected windows.
6. Click **Open Parcel** when u wanna get back to that setup.

WorkParcel can save normal web links and open them in your default browser. It doesn’t automatically capture separate Chrome or Edge tabs tho.

Also, removing a file or folder from a parcel only removes it from WorkParcel. It doesn’t delete the actual thing from your computer.

## Download

The current version is `v0.2.0-beta.3` and it works on Windows 10/11 (x64).

Download this from the Releases page:

`WorkParcel-Setup-0.2.0-beta.3.exe`

There’s also a portable ZIP for ppl who wanna try it without installing.

This is still a beta version, so there might be a few bugs. If u find smth broken, u can report it through GitHub Issues.

## Run from source

You’ll need Windows and the .NET 10 SDK.

```powershell
dotnet restore .\WorkParcel.slnx
dotnet build .\WorkParcel.slnx
dotnet test .\WorkParcel.slnx
dotnet run --project .\src\WorkParcel.App\WorkParcel.App.csproj --no-restore -p:WindowsPackageType=None -p:EnableMsixTooling=false -p:EnableWinAppRunSupport=false
```

## Made using

* C#
* .NET 10
* WinUI 3
* SQLite
