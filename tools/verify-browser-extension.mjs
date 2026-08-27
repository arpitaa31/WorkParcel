import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";

const extensionRoot = new URL("../browser-extension/", import.meta.url);
const manifest = JSON.parse(fs.readFileSync(new URL("manifest.json", extensionRoot), "utf8"));
assert.equal(manifest.manifest_version, 3, "the bridge must use Manifest V3");
assert.deepEqual([...manifest.permissions].sort(), ["nativeMessaging", "storage", "tabGroups", "tabs"], "the bridge must request only its documented permissions");
assert.equal(manifest.incognito, "not_allowed", "private browsing must remain disabled by default");
assert.equal(manifest.host_permissions, undefined, "the bridge must not request broad host permissions");
assert.equal(manifest.content_scripts, undefined, "the bridge must not inject content scripts");
assert.equal(manifest.background?.service_worker, "background.js", "the MV3 worker must be wired through the manifest");
for (const iconPath of Object.values(manifest.icons ?? {})) assert.ok(fs.existsSync(new URL(iconPath, extensionRoot)), `missing extension icon: ${iconPath}`);

const listeners = [];
const tabGroups = new Map([[5, { title: "Research", color: "blue" }]]);
const tabs = [
  { id: 1, windowId: 10, index: 0, url: "https://example.com/a", title: "A", favIconUrl: "https://example.com/a.ico", pinned: true, active: true, incognito: false, groupId: 5 },
  { id: 2, windowId: 10, index: 1, url: "https://private.example/a", title: "Private", pinned: false, active: false, incognito: true, groupId: -1 },
  { id: 3, windowId: 10, index: 2, url: "chrome://settings", title: "Settings", pinned: false, active: false, incognito: false, groupId: -1 },
  { id: 4, windowId: 11, index: 0, url: "https://example.com/b", title: "B", pinned: false, active: true, incognito: false },
  { id: 5, windowId: 12, index: 0, url: "https://private-only.example/", title: "Private only", pinned: false, active: true, incognito: true, groupId: -1 }
];
const storage = new Map();
let storageWritesFail = false;
storage.set("workParcelHandledRequests", [{ id: "persisted-request", expiresAt: Date.now() + 30000 }]);
const posted = [];
const removed = [];
let nextTabId = 20;
let nextWindowId = 99;
const disconnectCallbacks = [];

const event = () => ({ addListener: callback => listeners.push(callback) });
const port = {
  onMessage: event(),
  onDisconnect: { addListener: callback => disconnectCallbacks.push(callback) },
  postMessage: message => posted.push(message),
  disconnect: () => { for (const callback of disconnectCallbacks) callback(); }
};

const chrome = {
  runtime: {
    connectNative: () => port,
    lastError: null,
    onStartup: event(),
    onInstalled: event(),
    onMessage: event()
  },
  storage: {
    local: {
      get: async keys => {
        if (Array.isArray(keys)) return Object.fromEntries(keys.map(key => [key, storage.get(key)]));
        return { [keys]: storage.get(keys) };
      },
      set: async values => { if (storageWritesFail) throw new Error("storage unavailable"); for (const [key, value] of Object.entries(values)) storage.set(key, value); }
    }
  },
  tabs: {
    query: async () => tabs.slice(),
    get: async id => {
      const tab = tabs.find(candidate => candidate.id === id);
      if (!tab) throw new Error("tab not found");
      return tab;
    },
    remove: async id => {
      const index = tabs.findIndex(candidate => candidate.id === id);
      if (index < 0) throw new Error("tab not found");
      tabs.splice(index, 1); removed.push(id);
    },
    create: async options => {
      const tab = { id: nextTabId++, windowId: options.windowId, index: options.index ?? 0, url: options.url, title: options.url, pinned: !!options.pinned, active: !!options.active, incognito: false, groupId: -1 };
      tabs.push(tab);
      return tab;
    },
    move: async () => undefined,
    update: async (id, changes) => { const tab = tabs.find(candidate => candidate.id === id); if (tab) Object.assign(tab, changes); return tab; },
    group: async ({ tabIds }) => { for (const id of tabIds) { const tab = tabs.find(candidate => candidate.id === id); if (tab) tab.groupId = 8; } return 8; },
    onCreated: event(), onRemoved: event(), onUpdated: event()
  },
  tabGroups: {
    get: async id => tabGroups.get(id),
    update: async () => undefined
  },
  windows: {
    create: async options => {
      const windowId = nextWindowId++;
      const tab = { id: nextTabId++, windowId, index: 0, url: options.url, title: options.url, pinned: false, active: true, incognito: false, groupId: -1 };
      tabs.push(tab);
      return { id: windowId };
    },
    onCreated: event(), onRemoved: event()
  }
};

const context = vm.createContext({ chrome, navigator: { userAgent: "Mozilla/5.0 Chrome/140.0" }, console, setTimeout, clearTimeout, URL, Date, Map, Set, Promise });
vm.runInContext(fs.readFileSync(new URL("background.js", extensionRoot), "utf8"), context, { filename: "background.js" });
await new Promise(resolve => setTimeout(resolve, 10));
await context.onNativeMessage({ version: 1, type: "connection_status", requestId: "hello", browser: "chrome", connectionId: "test-connection", extensionVersion: "0.1.0", payload: { status: "CONNECTED", connectionId: "test-connection" } });
await context.onNativeMessage({ version: 1, type: "connection_status", requestId: "version-status", browser: "chrome", connectionId: "test-connection", extensionVersion: "0.1.0", payload: { status: "VERSION_MISMATCH", connectionId: "test-connection" } });
assert.equal(vm.runInContext("state.status", context), "VERSION MISMATCH", "protocol mismatch status is presented with user-facing spacing");
await context.onNativeMessage({ version: 1, type: "connection_status", requestId: "hello-again", browser: "chrome", connectionId: "test-connection", extensionVersion: "0.1.0", payload: { status: "CONNECTED", connectionId: "test-connection" } });
assert.equal(context.acceptRequest("persisted-request"), false, "recent command IDs survive a worker restart window");
context.persistState();
await new Promise(resolve => setTimeout(resolve, 10));
assert.equal(storage.get("workParcelConnection").connectionId, undefined, "temporary pipe connection IDs are not persisted");
await assert.rejects(() => context.sendToApp("refresh", {}, 20), /Connection timeout/);
await new Promise(resolve => setTimeout(resolve, 10));
assert.equal(storage.get("workParcelConnection").status, "CONNECTION ERROR", "request timeouts surface a recoverable connection error");
vm.runInContext("clearTimeout(reconnectTimer); reconnectTimer = null;", context);
await context.onNativeMessage({ version: 1, type: "error", requestId: "diagnostic-error", browser: "chrome", connectionId: "test-connection", extensionVersion: "0.1.0", payload: { code: "connection_error", message: "diagnostic failure" } });
assert.equal(vm.runInContext("state.status", context), "CONNECTION ERROR", "native connection errors surface in the popup state");
storageWritesFail = true;
assert.doesNotThrow(() => context.persistState(), "storage write failures must not escape the worker");
await new Promise(resolve => setTimeout(resolve, 10));
storageWritesFail = false;

const snapshot = await context.collectSnapshot();
assert.equal(snapshot.browser, "chrome");
assert.equal(snapshot.tabs.length, 3, "incognito tabs are excluded while unsupported HTTP-ineligible tabs remain marked");
assert.equal(snapshot.windowCount, 2, "private-only windows cannot affect visible window grouping");
assert.equal(snapshot.tabs.find(tab => tab.sessionTabId === "3").canRestore, false);
assert.equal(snapshot.tabs.find(tab => tab.sessionTabId === "1").groupTitle, "Research");
assert.equal(snapshot.tabs.find(tab => tab.sessionTabId === "4").sessionGroupId, null, "tabs without a group id remain explicitly ungrouped");
assert.equal(vm.runInContext("isRestorable('https://user:password@example.com/private')", context), false, "credential-bearing URLs are not restorable");
assert.equal(vm.runInContext("safeFavicon('https://user:password@example.com/icon.png')", context), null, "credential-bearing favicon references are rejected");

const stale = await context.closeTabs([{ id: "stale", sessionTabId: "1", sessionWindowId: "10", browser: "chrome", connectionId: "test-connection", expectedUrl: "https://changed.example/" }]);
assert.equal(stale.results[0].status, "Stale");
assert.deepEqual(removed, []);
const missingIdentity = await context.closeTabs([{ id: "missing-identity", sessionTabId: "1", sessionWindowId: "10", browser: "chrome", expectedUrl: "https://example.com/a" }]);
assert.equal(missingIdentity.results[0].status, "Stale", "close requests without the live connection identity are rejected");
assert.deepEqual(removed, []);
const missingExpectedUrl = await context.closeTabs([{ id: "missing-url", sessionTabId: "1", sessionWindowId: "10", browser: "chrome", connectionId: "test-connection" }]);
assert.equal(missingExpectedUrl.results[0].status, "Stale", "close requests without an expected URL are rejected");
assert.deepEqual(removed, []);
const unsupportedClose = await context.closeTabs([{ id: "internal", sessionTabId: "3", sessionWindowId: "10", browser: "chrome", connectionId: "test-connection", expectedUrl: "chrome://settings" }]);
assert.equal(unsupportedClose.results[0].status, "Unsupported");
assert.deepEqual(removed, []);

const windowBeforeDuplicate = nextWindowId;
const duplicate = await context.openTabs([{ id: "duplicate", url: "https://example.com/a", browserWindowGroupId: "window-0", allowDuplicate: false }]);
assert.equal(duplicate.results[0].status, "AlreadyOpen");
assert.equal(nextWindowId, windowBeforeDuplicate, "already-open restore does not create a new browser window");
const canonicalDuplicate = await context.openTabs([{ id: "canonical-duplicate", url: "HTTPS://EXAMPLE.COM/a", browserWindowGroupId: "window-0", allowDuplicate: false }]);
assert.equal(canonicalDuplicate.results[0].status, "AlreadyOpen", "equivalent host/scheme casing does not create a duplicate tab");
const reorderedWindowBeforeDuplicate = nextWindowId;
const reorderedWindowDuplicate = await context.openTabs([{ id: "reordered-window", url: "https://example.com/a", browserWindowGroupId: "window-99", allowDuplicate: false }]);
assert.equal(reorderedWindowDuplicate.results[0].status, "AlreadyOpen", "duplicate detection follows the live matching window even when logical window ordering changed");
assert.equal(nextWindowId, reorderedWindowBeforeDuplicate, "reordered-window duplicate does not create a new browser window");
const separateGroupWindowBefore = nextWindowId;
const separateGroupCopies = await context.openTabs([
  { id: "separate-group-one", url: "https://same-url.example/", browserWindowGroupId: "saved-window-a", allowDuplicate: false },
  { id: "separate-group-two", url: "https://same-url.example/", browserWindowGroupId: "saved-window-b", allowDuplicate: false }
]);
assert.equal(Array.from(separateGroupCopies.results, result => result.status).join(","), "Opened,Opened", "identical URLs in separate saved groups remain separate");
assert.equal(nextWindowId, separateGroupWindowBefore + 2, "separate saved groups receive separate browser windows");

const privateOnlyBeforeRestore = nextWindowId;
const privateOnlyRestore = await context.openTabs([{ id: "private-only", url: "https://private-only.example/", browserWindowGroupId: "window-2", allowDuplicate: false }]);
assert.equal(privateOnlyRestore.results[0].status, "Opened", "incognito-only windows cannot satisfy restore duplicate checks");
assert.equal(nextWindowId, privateOnlyBeforeRestore + 1);

const withinRequestBefore = nextWindowId;
const withinRequest = await context.openTabs([
  { id: "batch-1", url: "https://batch.example/", browserWindowGroupId: "window-7", allowDuplicate: false },
  { id: "batch-2", url: "https://batch.example/", browserWindowGroupId: "window-7", allowDuplicate: false }
]);
assert.equal(Array.from(withinRequest.results, result => result.status).join(","), "Opened,AlreadyOpen");
assert.equal(nextWindowId, withinRequestBefore + 1, "restore does not open duplicate URLs introduced by the same request");

const close = await context.closeTabs([{ id: "current", sessionTabId: "1", sessionWindowId: "10", browser: "chrome", connectionId: "test-connection", expectedUrl: "https://example.com/a" }]);
assert.equal(close.results[0].status, "Closed");
assert.deepEqual(removed, [1]);
const missingClose = await context.closeTabs([{ id: "missing", sessionTabId: "1", sessionWindowId: "10", browser: "chrome", connectionId: "test-connection", expectedUrl: "https://example.com/a" }]);
assert.equal(missingClose.results[0].status, "Stale", "a missing tab identity is rejected instead of counted as still open");

const opened = await context.openTabs([{ id: "saved", url: "https://restore.example/", browserWindowGroupId: "window-2", tabIndex: 0, pinned: true, browserTabGroupId: "group-2", browserTabGroupTitle: "Restored", browserTabGroupColor: "green" }]);
assert.equal(opened.results[0].status, "Opened");
assert.ok(tabs.some(tab => tab.url === "https://restore.example/"));

console.log("browser-extension manifest and fake API smoke passed: MV3 permissions, timeout/storage failure states, snapshot, private/internal exclusion, stale close, safe close, open and group metadata");
