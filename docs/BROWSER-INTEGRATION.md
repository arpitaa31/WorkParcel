# WorkParcel browser integration

WorkParcel uses one Chromium Manifest V3 extension source tree in both Google Chrome and Microsoft Edge. The extension is a small bridge, not a second WorkParcel client: it reports live tab metadata, asks the app to open saved tabs, and performs explicit close requests. The app remains the owner of parcel storage and policy.

## Components and trust boundaries

- `browser-extension/` contains the MV3 manifest, popup, icons, and service worker. It uses `tabs`, `tabGroups`, `nativeMessaging`, and `storage`, explicitly declares `incognito: not_allowed`, and has no content scripts, broad host permissions, history/cookies/password/download permissions, downloads, screenshots, or page-content access.
- `WorkParcel.BrowserHost` is launched by Chrome or Edge through official native messaging. The browser supplies the calling extension origin to the host and enforces the exact `allowed_origins` list in the registered host manifest; the host validates that origin again before forwarding data. It reads and writes the official 4-byte little-endian length-prefixed JSON framing and forwards validated messages to the app's current-user-only named pipe.
- `BrowserIntegrationService` owns the app-side pipe server, correlates request IDs, enforces protocol version and size limits, maintains independent Chrome and Edge sessions, and rejects stale browser connection identities.
- SQLite stores stable parcel metadata. Temporary browser tab/window IDs and pipe connection IDs are runtime-only and are never written to the database.

The protocol is version `1`, capped at 1 MiB per frame and 500 tabs per snapshot. Messages are typed (`hello`, `connection_status`, `list_windows`, `list_tabs`, `request_snapshot`, `tab_snapshot`, `open_tabs`, `close_tabs`, `operation_result`, `error`, `ping`, `pong`, and `open_workparcel`); unknown types, unsupported versions, invalid request IDs, malformed JSON, incomplete frames, and browser identities other than Chrome or Edge are rejected. The app reports the negotiated protocol version and compatibility in Settings and rejects an incompatible extension version instead of labeling it connected. Restore and close operations yield between bounded 50-tab batches so larger parcels do not monopolise the MV3 worker.

The implementation follows the current platform contracts: [Chrome Manifest V3 service workers](https://developer.chrome.com/docs/extensions/develop/migrate/what-is-mv3), the [extension service-worker lifecycle](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle), [Chrome native messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging), the [Chrome Windows API](https://developer.chrome.com/docs/extensions/reference/api/windows), [Chrome Tabs](https://developer.chrome.com/docs/extensions/reference/api/tabs), [Chrome tab groups](https://developer.chrome.com/docs/extensions/reference/api/tabGroups), and [Edge Chromium extension/native-messaging guidance](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging). Edge uses the same Chromium extension APIs and an independently registered exact extension origin.

The extension verifier checks the MV3 manifest, exact permissions, incognito policy, absence of broad host/content-script access, service-worker wiring, and required icons before running its fake-browser smoke scenarios.
The same smoke run verifies that request timeouts surface as `CONNECTION ERROR`, storage failures stay best-effort, and the worker remains eligible for its normal reconnect path.

## Setup (user-driven, reversible)

1. Build the solution: `dotnet build .\\WorkParcel.slnx`.
2. In Chrome, open `chrome://extensions`; in Edge, open `edge://extensions`. Enable Developer mode and choose **Load unpacked**. Select the `browser-extension` folder. The same source may be loaded independently in both browsers.
3. Copy each exact 32-character extension ID shown by the browser.
4. Build and launch WorkParcel using the unpackaged profile. The explicit profile properties are important because a prior packaged build must not be reused:

```powershell
dotnet run --project .\\src\\WorkParcel.App\\WorkParcel.App.csproj --no-restore `
  --launch-profile 'WorkParcel.App (Unpackaged)' `
  -p:EnableMsixTooling=false -p:EnableWinAppRunSupport=false
```

5. Register only the browsers you loaded, for the current Windows user:

The unpackaged profile sets `WindowsAppSDKSelfContained=true` automatically, so the command carries the Windows App SDK runtime and does not require a machine-registered framework package.

```powershell
.\\tools\\Setup-BrowserHost.ps1 -Browser Chrome `
  -HostExecutablePath .\\src\\WorkParcel.BrowserHost\\bin\\Debug\\net10.0\\WorkParcel.BrowserHost.exe `
  -ChromeExtensionId <chrome-id>

.\\tools\\Setup-BrowserHost.ps1 -Browser Edge `
  -HostExecutablePath .\\src\\WorkParcel.BrowserHost\\bin\\Debug\\net10.0\\WorkParcel.BrowserHost.exe `
  -EdgeExtensionId <edge-id>
```

`-Browser Both` accepts both IDs in one call. Repeating setup separately for Chrome and Edge merges the exact origins instead of overwriting the other browser. The script writes the host manifest and exact `allowed-origins.json` beside the executable and edits only the current user's Chrome/Edge NativeMessagingHosts key. It never installs an extension, changes browser policy, launches an arbitrary URL, or requires administrator rights. Remove one registration with the same host path, its exact ID, and `-Browser Chrome -Remove -ChromeExtensionId <chrome-id>` (or the matching Edge parameters); use `Both` to remove both registrations and the generated sidecar files.

## Popup and status meanings

The compact popup shows `WORKPARCEL`, the detected browser, connection state, current window/tab counts, **OPEN WORKPARCEL**, and **REFRESH CONNECTION**. The app Settings page shows the same information separately for Chrome and Edge, plus browser installed state, host registration, extension version, protocol compatibility, last connection, and actions for setup, repair/help, extension folder, test, disconnect, and remove registration.

States are honest and actionable:

- `CONNECTED — CHROME`, `CONNECTED — EDGE`: live native bridge; counts are current non-private tabs.
- `CONNECTING`: the MV3 worker is reconnecting with bounded backoff.
- `APP NOT RUNNING` / `APP CONNECTION LOST`: the native host exists but the WorkParcel pipe is unavailable; start or relaunch WorkParcel.
- `EXTENSION NOT DETECTED`: host registration exists but this browser has not connected; reload the exact unpacked extension ID.
- `NATIVE HOST NOT INSTALLED`: the current user's registration or executable is missing.
- `VERSION MISMATCH` or `CONNECTION ERROR`: reload matching extension/host binaries and use **REPAIR CONNECTION**.
- `BROWSER NOT FOUND`: the standard Chrome/Edge executable is not installed.

## Capture, save, open, and pack away

Capture queries live browser tabs with the official `tabs` API and displays them as Browser → window → tab group → tab. Each selectable row shows browser family, window group, tab-group title/color when available, title, domain, URL, pinned/active state, and a fallback icon. Each window has **SELECT WINDOW** and **CLEAR WINDOW**. Private/incognito and browser-internal/unsupported URLs are excluded before persistence and are counted as excluded; no private-browsing permission is requested.

Save/update is transactional and writes one summary history event. Stable fields include browser family, domain, URL (including query/path/fragment and HTTP-versus-HTTPS), logical saved window group, saved position, pinned/active flags, tab-group metadata, favicon reference, and timestamps. The current session tab/window/connection IDs are refreshed on each snapshot and are not persisted.

Parcel Details distinguishes Browser Tabs from manual Web Links, with direct **ADD LINK MANUALLY**, **PASTE LINKS**, and **CAPTURE OPEN TABS** actions. It searches browser/domain/window-group metadata, filters Chrome or Edge tabs, and sorts by saved position. Open requests are grouped into separate browser windows, restore saved order/pinned state/tab groups/active tab where the browser permits, skip unsupported and already-open URLs with an honest result, and never claim to restore forms, login sessions, history, cookies, or page state. Opens may retry on the current connection after an extension reload; closes require the exact captured connection identity. Pack Away saves first; only after a second confirmation does it request close for explicitly checked current tabs/windows. The confirmation warns that webpages may contain unsaved work. Closing validates browser, connection ID, current window ID, and exact expected URL; stale or mismatched selections are rejected and no browser window/process is closed.

## Security and privacy boundaries

No network HTTP server or localhost endpoint is used. The named pipe is asynchronous and current-user-only, with bounded reads, writes, timeouts, cancellation, and multiple independent connections. URLs never become shell commands or process arguments. Logs contain event/type/error information only and avoid private URLs, titles, cookies, page content, or credentials. Favicon handling uses the browser-provided reference with a fallback glyph; WorkParcel does not download arbitrary page content.

## Known limitations and deliberately deferred work

- Chrome and Edge are supported; Firefox and other browsers are deliberately deferred for Part 4.
- The extension is currently loaded unpacked for development. Chrome Web Store and Edge Add-ons publication, signing, and silent installation are deliberately deferred.
- The browser APIs can restore URL, title metadata, window placement, order, pinning, tab groups, and an active tab where supported. They cannot restore form contents, scroll position, unsaved webpage data, login state, browser history, cookies, or complete session memory.
- Private/incognito tabs and browser-internal pages are excluded by default and are never silently stored or closed.
- Snapshots and operations are capped at 500 tabs and processed in batches; very large browser workspaces must be handled in multiple passes.
- Favicon downloads are deliberately not part of core capture. Saved browser tabs use a safe browser/domain fallback badge, so offline operation does not depend on network access.
- A live Chrome/Edge acceptance pass requires a browser connector and disposable tabs. If that connector is unavailable, the deterministic protocol, fake-browser, native-host, persistence, and unpackaged-launch checks remain the available local evidence; the final report must say that live browser interaction was not observed.

## Verification checklist

Run `dotnet test .\\tests\\WorkParcel.Tests\\WorkParcel.Tests.csproj`, `node --check browser-extension\\background.js`, `node tools\\verify-browser-extension.mjs`, `node tools\\verify-browser-host.mjs`, and `dotnet build .\\WorkParcel.slnx`. The fake-browser smoke test uses no browser profile or network and covers snapshot grouping, private/internal exclusion, stale-close rejection, safe close, opening, and tab-group metadata. The native-host smoke test launches the host with an explicit diagnostic no-origin flag and verifies framed app-pipe relay; production launches require the browser-supplied extension origin, which is validated against `allowed_origins.json`. For a manual browser pass, load the unpacked extension in Chrome and Edge, verify the popup state transition, open disposable HTTP tabs in two windows and a tab group, capture and restart the app, verify persistence/order/pinned/group metadata, test duplicate rules and unsupported/internal/private exclusion, open with an already-open tab, disconnect/reconnect each browser independently, test stale close after refresh, and confirm Pack Away saves before its close warning. Also verify that changing theme/resize does not clip the capture tree, parcel details, or settings actions.

If the in-app browser connector is unavailable, static checks, protocol framing tests, a named-pipe handshake, and the unpackaged development launch provide deterministic local verification; the final report must call out that live Chrome/Edge interaction was not observed rather than implying it was. On managed machines where Application Control blocks the packaged MSIX debug payload, use the unpackaged command above for local launch testing. If that machine also blocks unsigned unpackaged binaries, use a policy-approved/signed build.
