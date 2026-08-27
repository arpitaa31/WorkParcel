# WorkParcel

### Pack your workspace. Open it when you return.

While working on smth, I usually have a bunch of files, folders, apps and browser tabs open.

Once I close everything, finding and opening the whole setup again is sooo annoying.

That’s why I made WorkParcel.

It lets you save your whole setup inside a **parcel**, pack it away and open it again whenever you wanna continue.

## Download

WorkParcel is made for Windows.

The installer is added to GitHub Releases. After installing it, you’ll be able to open WorkParcel normally from your Desktop, Start Menu or Windows Search.
A portable ZIP version will also be available.

## How does it work?

### 1. Create a parcel

Give your setup a name, like:

* WorkParcel Development
* School Project
* Website Work
* Video Editing

### 2. Choose your stuff

You can select exactly what you wanna save:

* Open apps and windows
* Files
* Folders
* Applications
* Website links
* Browser tabs
* Notes

Everything has a checkbox, so unrelated apps won’t be added unless you select them.

### 3. Pack it away

Review everything and click **Pack Away**.

The parcel gets saved locally and stays there even after restarting the app or computer.

### 4. Open it again

Whenever you wanna continue, open the parcel and choose which items you wanna launch.

Files open in their usual apps, folders open in File Explorer, links open in your browser and supported apps launch again.

## Desk Memory

Desk Memory also remembers where your app windows were placed.

It can save:

* Window position
* Window size
* Maximised or normal state
* Which monitor it was on

So if VS Code was on the left, Chrome was on another monitor and Terminal was underneath, WorkParcel can bring them back to the same layout.
If a saved monitor isn’t connected, it safely moves those windows to an available screen instead of leaving them off-screen.

## Main features

* Select open apps and windows
* Save website links and notes
* Pack everything into one parcel
* Reopen the setup later
* Remember window layouts with Desk Memory
* Simple Today to-do list
* Archive and restore parcels
* Dark and light themes
* Local SQLite storage

## Is it safe?

WorkParcel does not move, copy or delete your real files.
Removing a file or folder from a parcel only removes its WorkParcel record.
If you choose **Save and Close**, WorkParcel asks supported windows to close normally. It does not force-kill apps or skip their unsaved-work warnings.
Everything is stored locally. WorkParcel does not read browser history, passwords, cookies or webpage content.

## Browser tabs

Automatic tab capture needs the optional WorkParcel extension for Chrome or Edge.
It lets you select particular tabs, save them inside a parcel and reopen them later.
The extension is not on the browser stores yet, so it currently has to be installed manually.
Manual website links work without the extension.

## Run from source

You’ll need Windows and the .NET 10 SDK.

```powershell
git clone https://github.com/arpitaa31/WorkParcel.git
cd WorkParcel
dotnet restore .\WorkParcel.slnx
dotnet build .\WorkParcel.slnx
dotnet test .\WorkParcel.slnx
dotnet run --project .\src\WorkParcel.App\WorkParcel.App.csproj
```

## Built with

* C#
* .NET 10
* WinUI 3
* Windows App SDK
* SQLite
* Win32 APIs
* Chromium extension

# Conclusion
WorkParcel started as a simple idea for saving a few files and apps together, but now it’s slowly becoming a proper Windows workspace manager :D And i myself would want to use this daily, cuz editing and working on diff tasks with sooo many tabs open, and then after closing them, reopening them is a big task!
