# WorkParcel browser-extension setup

The WorkParcel Chromium bridge is optional. WorkParcel remains fully usable for files, folders, applications, links, notes, parcels, Today tasks, Archive, and Restore without it. The extension is not published in the Chrome Web Store or Microsoft Edge Add-ons in version 0.1.0.

## Install the optional extension

1. Install WorkParcel, or extract the portable build.
2. Open `chrome://extensions` in Chrome or `edge://extensions` in Edge.
3. Turn on Developer mode and choose **Load unpacked**.
4. Select the installed `browser-extension` folder:
   `%LOCALAPPDATA%\Programs\WorkParcel\browser-extension`
   For the portable build, select the `browser-extension` folder inside the extracted folder.
5. Copy the exact 32-character extension ID shown by the browser. Chrome and Edge may show different IDs; register each exact ID separately.

## Register or repair the native host

Open PowerShell as the current Windows user and run the command for each browser. The commands use the installed files and write only a current-user registration. They do not install the extension or use wildcard origins.

```powershell
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\WorkParcel'
$setup = Join-Path $installRoot 'BrowserHost\Setup-BrowserHost.ps1'
$hostExecutablePath = Join-Path $installRoot 'BrowserHost\WorkParcel.BrowserHost.exe'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $setup -Browser Chrome -HostExecutablePath $hostExecutablePath -ChromeExtensionId '<exact-chrome-id>'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $setup -Browser Edge -HostExecutablePath $hostExecutablePath -EdgeExtensionId '<exact-edge-id>'
```

Rerun the same command after reloading the unpacked extension or after an application update. In WorkParcel, open **Settings -> BROWSER TABS** and use the guided **CONNECT CHROME** or **CONNECT EDGE** flow. The browser card's **ADVANCED DETAILS** section retains **Repair connection**, **View setup help**, and **Test connection** when manual registration is needed. Restart the browser if it has cached the previous host registration.

## Remove registration

Use the same installed paths and exact ID, adding `-Remove`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $setup -Browser Chrome -HostExecutablePath $hostExecutablePath -ChromeExtensionId '<exact-chrome-id>' -Remove
```

Use the matching Edge parameters for Edge, or `-Browser Both` with both exact IDs. In WorkParcel, **Settings -> BROWSER TABS -> ADVANCED DETAILS -> Remove connection** removes the current-user registration after confirmation; it does not remove the browser extension.

The native host allows only the exact `chrome-extension://<id>/` origins supplied to the setup script. No browser profile, cookies, page contents, or credentials are packaged or read.
