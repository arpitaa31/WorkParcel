# WorkParcel installation troubleshooting

## Windows warns about the installer

WorkParcel 0.2.0 Beta 3 is not code-signed. Windows SmartScreen may therefore show an unfamiliar-app warning. Confirm that the file came from the release source, inspect More info and the displayed publisher/signature details, and decide whether you trust that specific file. Do not disable SmartScreen or antivirus protection. SHA-256 values are provided in SHA256SUMS.txt.

If Smart App Control or an enterprise Code Integrity policy requires signed executables, it may block this unsigned setup before its window appears. Use an approved signed build or ask the device administrator to apply the organization-approved exception; do not weaken Windows security controls just to run this build.

## The installer does not start

Use a Windows 10/11 x64 machine. Windows 10 version 1809 (build 17763) or later is required. The installer is per-user and normally installs to %LOCALAPPDATA%\Programs\WorkParcel; it should not require administrator access. If WorkParcel is open during an update, close it and retry.

## WorkParcel opens from the installer but not from a shortcut

Confirm that %LOCALAPPDATA%\Programs\WorkParcel\WorkParcel.exe exists. Repair by running the installer again. The Start Menu and optional Desktop shortcuts should point to that executable with the installed folder as the working directory; they do not depend on the repository, VS Code, PowerShell, or dotnet run.

## Local data or parcels are missing

WorkParcel stores the database at %LOCALAPPDATA%\WorkParcel\Data\workparcel.db and logs at %LOCALAPPDATA%\WorkParcel\Logs\workparcel.log. The install directory is not the data directory. Reinstall and ordinary uninstall do not delete this data. Do not copy a developer database into the installation folder.

## Web links do not open

Confirm that the saved value begins with http:// or https:// and has a host. WorkParcel sends each valid URL to the Windows default browser and reports failures per item, so one unavailable link does not prevent the other selected items from opening. It does not fetch pages while saving links.

## Uninstall and data preservation

Uninstall removes the application files, shortcuts, and uninstall entry. It intentionally preserves %LOCALAPPDATA%\WorkParcel so parcels and backups can be loaded after reinstall. Delete that folder only after making a separate backup and only when you explicitly want to remove all local data.
