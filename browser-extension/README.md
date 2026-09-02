# WorkParcel Chromium bridge

This single Manifest V3 extension runs in Google Chrome and Microsoft Edge. Its service worker uses only `tabs`, `tabGroups`, `nativeMessaging`, and `storage` to report live metadata and perform explicit, identity-checked open/close operations. The manifest explicitly disallows incognito contexts. It has no content scripts, broad host permissions, history/cookies/password/download access, page-content reads, screenshots, keystrokes, or private-browsing permission.

## Load and register

1. Build `WorkParcel.slnx`.
2. Load this directory as an unpacked extension from `chrome://extensions` and/or `edge://extensions` with Developer mode enabled.
3. Copy each exact 32-character ID and run `tools\\Setup-BrowserHost.ps1` with the matching `-Browser` and extension-ID parameter. The browser enforces the exact ID in the native-host manifest's `allowed_origins`; registration is current-user-only and reversible, and does not install the extension or bypass browser prompts.
4. Launch WorkParcel with the unpackaged profile (use `--no-restore` when dependencies are already restored): `dotnet run --project .\\src\\WorkParcel.App\\WorkParcel.App.csproj --no-restore --launch-profile "WorkParcel.App (Unpackaged)" -p:EnableMsixTooling=false -p:EnableWinAppRunSupport=false`. This profile carries the Windows App SDK runtime locally, so it does not require a machine-registered framework package. The popup reports `CONNECTED`, `APP NOT RUNNING`, `HOST NOT INSTALLED`, `EXTENSION NOT DETECTED`, `VERSION MISMATCH`, or `CONNECTION ERROR`, plus live window/tab counts.

The extension worker reconnects with bounded backoff, persists only minimal status plus a short-lived request-id de-duplication window in `chrome.storage.local`, and refreshes a capped snapshot when windows/tabs change. Private and browser-internal URLs are excluded. Saved tab state belongs to the WorkParcel SQLite database; browser session IDs are temporary and are never written there. When a saved URL is already open in the matching logical window, restore reuses that window and reports `AlreadyOpen` instead of creating an unnecessary duplicate window.

See [BROWSER-EXTENSION-SETUP.md](../docs/BROWSER-EXTENSION-SETUP.md) and [INSTALLATION-TROUBLESHOOTING.md](../docs/INSTALLATION-TROUBLESHOOTING.md) for setup, protocol framing, privacy boundaries, Pack Away close safety, troubleshooting, and the manual Chrome/Edge checklist.

For deterministic local checks without a browser profile, run `node tools\verify-browser-extension.mjs` and `node tools\verify-browser-host.mjs`. The first uses fake Chrome APIs to exercise snapshot grouping, private/internal exclusion, stale-close rejection, safe close, opening, and tab-group metadata; the second enables an explicit diagnostic no-origin mode to verify framed app-pipe relay. Production native-messaging launches require and validate the browser-supplied extension origin.
