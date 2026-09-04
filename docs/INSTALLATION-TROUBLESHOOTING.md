# WorkParcel installation troubleshooting

## Windows warns about the installer

WorkParcel 0.2.0 Beta is not code-signed. Windows SmartScreen may therefore show an unfamiliar-app warning. Confirm that the file came from the release source, inspect **More info** and the displayed publisher/signature details, and decide whether you trust that specific file. Do not disable SmartScreen or antivirus protection. SHA-256 values are provided in `SHA256SUMS.txt`.

If Smart App Control or an enterprise Code Integrity policy requires signed executables, it may block this unsigned setup before its window appears. Use an approved signed build or ask the device administrator to apply the organization-approved exception; do not weaken Windows security controls just to run this build.

## The installer does not start

Use a Windows 10/11 x64 machine. Windows 10 version 1809 (build 17763) or later is required. The installer is per-user and normally installs to `%LOCALAPPDATA%\Programs\WorkParcel`; it should not require administrator access. If WorkParcel is open during an update, close it and retry.

## WorkParcel opens from the installer but not from a shortcut

Confirm that `%LOCALAPPDATA%\Programs\WorkParcel\WorkParcel.exe` exists. Repair by running the installer again. The Start Menu and optional Desktop shortcuts should point to that executable with the installed folder as the working directory; they do not depend on the repository, VS Code, PowerShell, or `dotnet run`.

## Local data or parcels are missing

WorkParcel stores the database at `%LOCALAPPDATA%\WorkParcel\Data\workparcel.db` and logs at `%LOCALAPPDATA%\WorkParcel\Logs\workparcel.log`. The install directory is not the data directory. Reinstall and ordinary uninstall do not delete this data. Do not copy a developer database into the installation folder.

## Browser tabs are not connected

Open **Settings -> BROWSER TABS** and choose **CONNECT CHROME** or **CONNECT EDGE**. The guided flow detects the browser and shows the next required step. If manual extension loading is required, enable Developer mode, choose **Load unpacked**, select the installed `browser-extension` folder, and return to WorkParcel. The exact extension ID may be required by browser security; never use `*`. Reload the extension and restart the browser if necessary.

## The browser card says "Connection needs attention"

Choose **FIX CONNECTION**. If the problem remains, expand **ADVANCED DETAILS** and use **View details**, **Repair connection**, or **View setup help**. The bridge uses a current-user named pipe and does not require a public localhost server. WorkParcel can continue to manage non-browser parcel items if the bridge is unavailable.

## Uninstall and data preservation

Uninstall removes the application files, shortcuts, and uninstall entry. It intentionally preserves `%LOCALAPPDATA%\WorkParcel` so parcels and backups can be loaded after reinstall. Delete that folder only after making a separate backup and only when you explicitly want to remove all local data.
