# WorkParcel portable build

1. Extract `WorkParcel-Portable-0.2.0-beta.2-win-x64.zip` to a folder you control.
2. Open the extracted folder.
3. Double-click `WorkParcel.exe`.

This is a folder-based self-contained build, not a single-file executable. Keep the supporting files and folders beside `WorkParcel.exe`. It does not require the .NET SDK or `dotnet run`.

WorkParcel still stores user data in `%LOCALAPPDATA%\WorkParcel\Data\workparcel.db` and logs in `%LOCALAPPDATA%\WorkParcel\Logs`; it does not invent a portable-data mode. The optional unpacked browser extension and installed-path registration instructions are in `Documentation\Browser-Extension-Setup.md`.
