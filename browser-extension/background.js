const VERSION = 1;
const HOST = "com.workparcel.browser";
const MAX_TABS = 500;
const OPERATION_BATCH_SIZE = 50;
let port = null;
let state = { status: "APP NOT RUNNING", browser: detectBrowser(), extensionVersion: "1.0.0", lastConnected: null, windowCount: 0, tabCount: 0 };
let reconnectTimer = null;
let sequence = 0;
let disconnectOverride = null;
const pending = new Map();
const handledRequests = new Map();
const persistedHandledRequests = new Map();
function pruneHandledRequests() { const now = Date.now(); for (const [id, expiresAt] of persistedHandledRequests) if (expiresAt <= now) persistedHandledRequests.delete(id); }
function safeStorageSet(value) { try { const write = chrome.storage.local.set(value); if (write && typeof write.catch === "function") write.catch(() => { }); } catch { } }
function persistHandledRequests() { try { pruneHandledRequests(); safeStorageSet({ workParcelHandledRequests: [...persistedHandledRequests].map(([id, expiresAt]) => ({ id, expiresAt })).slice(-100) }); } catch { } }
function acceptRequest(id) {
  pruneHandledRequests();
  if (!id || handledRequests.has(id) || persistedHandledRequests.has(id)) return false;
  const expiresAt = Date.now() + 30000; handledRequests.set(id, Date.now()); persistedHandledRequests.set(id, expiresAt); persistHandledRequests();
  setTimeout(() => { handledRequests.delete(id); persistedHandledRequests.delete(id); persistHandledRequests(); }, 30000);
  return true;
}
function persistState() { try { safeStorageSet({ workParcelConnection: { status: state.status, browser: state.browser, lastConnected: state.lastConnected, windowCount: state.windowCount || 0, tabCount: state.tabCount || 0 } }); } catch { } }

function detectBrowser() { return /Edg\//i.test(navigator.userAgent) ? "edge" : "chrome"; }
function requestId() { sequence += 1; return `${Date.now()}-${sequence}`; }
function envelope(type, payload = {}, id = requestId()) { return { version: VERSION, requestId: id, type, browser: state.browser, connectionId: state.connectionId || null, extensionVersion: state.extensionVersion, timestampUtc: new Date().toISOString(), payload }; }
function recordConnectionError(message) { state.status = "CONNECTION ERROR"; state.lastError = message; persistState(); }
function displayStatus(value) { return String(value || "CONNECTION ERROR").replaceAll("_", " "); }
function postEnvelope(type, payload = {}, id = requestId()) {
  const channel = port; if (!channel) return false;
  try { channel.postMessage(envelope(type, payload, id)); return true; }
  catch { disconnectOverride = "Connection error"; recordConnectionError("The browser connection stopped while sending a response."); try { channel.disconnect?.(); } catch { } return false; }
}
async function sendSnapshotResponse(type, id) {
  try { postEnvelope(type, await collectSnapshot(), id); }
  catch { recordConnectionError("The browser snapshot could not be collected."); postEnvelope("error", { code: "connection_error", message: "The browser snapshot could not be collected." }, id); }
}
async function sendOperationResponse(operation, payload, id) {
  try { postEnvelope("operation_result", await operation(payload), id); }
  catch { recordConnectionError("The browser operation could not be completed."); postEnvelope("error", { code: "connection_error", message: "The browser operation could not be completed." }, id); }
}

function connect() {
  if (port) return port;
  try {
    port = chrome.runtime.connectNative(HOST); state.status = "CONNECTING";
    port.onMessage.addListener(onNativeMessage);
  port.onDisconnect.addListener(() => { const reason = disconnectOverride || chrome.runtime.lastError?.message || "Native host disconnected"; const explicitConnectionError = /connection\s+error|connection\s+timeout|could not send/i.test(reason); const extensionRejected = /forbidden|not allowed|access to the specified native messaging host/i.test(reason); disconnectOverride = null; port = null; state.status = /not found/i.test(reason) ? "HOST NOT INSTALLED" : extensionRejected ? "EXTENSION NOT DETECTED" : explicitConnectionError ? "CONNECTION ERROR" : /pipe|timeout|disconnected|exited|cannot connect|communicating with the native messaging host/i.test(reason) ? "APP NOT RUNNING" : "CONNECTION ERROR"; state.lastError = reason; persistState(); for (const entry of pending.values()) { clearTimeout(entry.timer); entry.reject(new Error(state.status)); } pending.clear(); scheduleReconnect(); });
    port.postMessage(envelope("hello", { browser: state.browser }));
  } catch (error) { const hadPort = port !== null; try { port?.disconnect?.(); } catch { } port = null; state.status = hadPort ? "CONNECTION ERROR" : "HOST NOT INSTALLED"; state.lastError = String(error); persistState(); scheduleReconnect(); }
  return port;
}
function scheduleReconnect() { if (reconnectTimer) return; reconnectTimer = setTimeout(() => { reconnectTimer = null; connect(); }, 5000); }
function sendToApp(type, payload = {}, timeout = 5000) {
  const channel = connect(); if (!channel) return Promise.reject(new Error(state.status));
  if (state.status !== "CONNECTED" || !state.connectionId) return Promise.reject(new Error("Connection is still connecting"));
  const id = requestId();
  return new Promise((resolve, reject) => { const timer = setTimeout(() => { pending.delete(id); disconnectOverride = "Connection timeout"; state.status = "CONNECTION ERROR"; state.lastError = "Connection timeout"; persistState(); try { channel.disconnect?.(); } catch { } reject(new Error("Connection timeout")); }, timeout); pending.set(id, { resolve, reject, timer }); try { channel.postMessage(envelope(type, payload, id)); } catch (error) { clearTimeout(timer); pending.delete(id); disconnectOverride = String(error); state.status = "CONNECTION ERROR"; state.lastError = "Connection could not send the request"; persistState(); try { channel.disconnect?.(); } catch { } reject(error); } });
}

async function onNativeMessage(message) {
  if (!message || message.version !== VERSION || typeof message.type !== "string") return;
  if (message.type === "connection_status") {
    const entry = pending.get(message.requestId); if (entry) { clearTimeout(entry.timer); pending.delete(message.requestId); entry.resolve(message); }
    state.status = displayStatus(message.payload?.status || "CONNECTED"); state.connectionId = message.payload?.connectionId || state.connectionId; if (state.status === "CONNECTED") { state.lastConnected = new Date().toISOString(); sendSnapshot(); } persistState();
    return;
  }
  if (["error", "operation_result"].includes(message.type)) {
    const entry = pending.get(message.requestId); if (entry) { clearTimeout(entry.timer); pending.delete(message.requestId); message.type === "error" ? entry.reject(new Error(message.payload?.message || "Browser connection error")) : entry.resolve(message); }
    if (message.type === "error") {
      const code = message.payload?.code || "";
      if (code === "version_mismatch" || code === "invalid_message" && /version/i.test(message.payload?.message || "")) state.status = "VERSION MISMATCH";
      else if (code === "connection_error") state.status = "CONNECTION ERROR";
      if (code === "version_mismatch" || code === "invalid_message" || code === "connection_error") { state.lastError = message.payload?.message || code; persistState(); }
    }
    return;
  }
  if (message.type === "list_tabs" && pending.has(message.requestId)) { const entry = pending.get(message.requestId); clearTimeout(entry.timer); pending.delete(message.requestId); entry.resolve(message); return; }
  if (message.type === "list_windows") { await sendSnapshotResponse("list_tabs", message.requestId); return; }
  if (message.type === "list_tabs" || message.type === "refresh_tabs") { await sendSnapshotResponse("list_tabs", message.requestId); return; }
  if (message.type === "request_snapshot") { try { const snapshot = await collectSnapshot(); postEnvelope("tab_snapshot", snapshot, message.requestId); postEnvelope("operation_result", { status: "OK", message: "Tab snapshot sent" }, message.requestId); } catch { recordConnectionError("The browser snapshot could not be collected."); postEnvelope("error", { code: "connection_error", message: "The browser snapshot could not be collected." }, message.requestId); } return; }
  if (message.type === "open_tabs") { if (!acceptRequest(message.requestId)) { postEnvelope("operation_result", { status: "Duplicate", results: [], message: "The request was already handled." }, message.requestId); return; } await sendOperationResponse(openTabs, message.payload?.tabs || [], message.requestId); return; }
  if (message.type === "close_tabs") { if (!acceptRequest(message.requestId)) { postEnvelope("operation_result", { status: "Duplicate", results: [], message: "The request was already handled." }, message.requestId); return; } await sendOperationResponse(closeTabs, message.payload?.tabs || [], message.requestId); return; }
}

function isRestorable(url) { try { const parsed = new URL(url); return (parsed.protocol === "http:" || parsed.protocol === "https:") && !parsed.username && !parsed.password; } catch { return false; } }
function canonicalUrl(url) { try { return new URL(url).href; } catch { return String(url || ""); } }
function safeFavicon(url) { try { const parsed = new URL(url || ""); return (parsed.protocol === "http:" || parsed.protocol === "https:") && !parsed.username && !parsed.password && url.length <= 2048 ? parsed.href : null; } catch { return null; } }
async function collectSnapshot() {
  const captured = new Date().toISOString(); const tabs = await chrome.tabs.query({}); const visibleTabs = tabs.filter(tab => !tab.incognito && tab.id !== undefined && tab.id !== null).sort((a, b) => Number(a.windowId) - Number(b.windowId) || Number(a.index) - Number(b.index) || Number(a.id) - Number(b.id)); const windowIds = [...new Set(visibleTabs.map(tab => tab.windowId))].sort((a, b) => Number(a) - Number(b)); const windowKeys = new Map(windowIds.map((id, index) => [id, `window-${index}`])); const windows = new Set(windowIds.map(id => String(id))); const output = [];
  for (const tab of visibleTabs) {
    if (output.length >= MAX_TABS) continue;
    const hasGroup = Number.isInteger(tab.groupId) && tab.groupId >= 0; let group = null; if (hasGroup) { try { group = await chrome.tabGroups.get(tab.groupId); } catch { } }
    const url = tab.url || ""; const supported = isRestorable(url); let domain = ""; try { domain = new URL(url).hostname; } catch { }
    windows.add(String(tab.windowId)); output.push({ sessionTabId: String(tab.id), sessionWindowId: String(tab.windowId), windowGroupKey: windowKeys.get(tab.windowId) || "window-0", sessionGroupId: hasGroup ? String(tab.groupId) : null, url, title: tab.title || domain || "Untitled tab", domain, browser: state.browser, connectionId: state.connectionId || null, tabIndex: tab.index, pinned: !!tab.pinned, active: !!tab.active, groupTitle: group?.title || null, groupColor: group?.color || null, faviconUrl: safeFavicon(tab.favIconUrl), incognito: !!tab.incognito, canRestore: supported, unsupportedReason: supported ? null : "Browser-internal or unsupported URL", capturedAtUtc: captured });
  }
  state.windowCount = windows.size; state.tabCount = output.length; if (port && state.status === "CONNECTED") persistState();
  return { browser: state.browser, windowCount: windows.size, tabs: output, capturedAtUtc: captured };
}
async function sendSnapshot() { if (!port) return; try { postEnvelope("tab_snapshot", await collectSnapshot()); } catch { recordConnectionError("The browser snapshot could not be collected."); } }

async function openTabs(savedTabs) {
  const results = []; let processed = 0; const yieldAfterBatch = async () => { processed += 1; if (processed % OPERATION_BATCH_SIZE === 0) await new Promise(resolve => setTimeout(resolve, 0)); }; const windows = new Map(); const grouped = new Map(); const activeByWindow = new Map(); const existing = (await chrome.tabs.query({})).filter(tab => !tab.incognito && tab.id !== undefined && tab.id !== null); const existingWindowIds = [...new Set(existing.map(tab => tab.windowId))].sort((a, b) => Number(a) - Number(b)); const existingByWindow = new Map(); const assignedExistingWindows = new Set(); const openedByWindow = new Map(); for (const tab of existing) { if (isRestorable(tab.url || "")) { const key = String(tab.windowId); if (!existingByWindow.has(key)) existingByWindow.set(key, new Set()); existingByWindow.get(key).add(canonicalUrl(tab.url)); } }
  const groupedItems = new Map(); for (const item of savedTabs.slice(0, MAX_TABS)) { const key = String(item?.browserWindowGroupId || item?.id || "window-0"); if (!groupedItems.has(key)) groupedItems.set(key, []); groupedItems.get(key).push(item); }
  for (const items of groupedItems.values()) items.sort((a, b) => Number(a?.tabIndex ?? Number.MAX_SAFE_INTEGER) - Number(b?.tabIndex ?? Number.MAX_SAFE_INTEGER) || String(a?.id || a?.url || "").localeCompare(String(b?.id || b?.url || "")));
  for (const [windowKey, windowItems] of groupedItems) {
    const first = windowItems.find(item => item && isRestorable(item.url));
    if (first && !windows.has(windowKey)) {
      const existingWindowId = existingWindowIds.find(id => !assignedExistingWindows.has(id) && existingByWindow.get(String(id))?.has(canonicalUrl(first.url)));
      const reuseExistingWindow = !first.allowDuplicate && existingWindowId !== undefined;
      if (reuseExistingWindow) { windows.set(windowKey, existingWindowId); assignedExistingWindows.add(existingWindowId); }
      else { try { const createdWindow = await chrome.windows.create({ focused: false, url: first.url }); if (createdWindow.id !== undefined && createdWindow.id !== null) windows.set(windowKey, createdWindow.id); } catch { } }
    }
    for (const item of windowItems) {
    if (!item || !isRestorable(item.url)) { results.push({ itemKey: item?.id || item?.url || "unknown", status: "Unsupported", message: "Only HTTP and HTTPS tabs can be restored" }); await yieldAfterBatch(); continue; }
    const targetWindowId = windows.get(windowKey); const itemUrlKey = canonicalUrl(item.url); const alreadyOpen = !item.allowDuplicate && ((targetWindowId !== undefined && existingByWindow.get(String(targetWindowId))?.has(itemUrlKey)) || openedByWindow.get(windowKey)?.has(itemUrlKey)); if (alreadyOpen) { results.push({ itemKey: item.id || item.url, status: "AlreadyOpen", message: "The same URL is already open in the captured browser window" }); await yieldAfterBatch(); continue; }
    try {
      const windowId = windows.get(windowKey); if (windowId === undefined || windowId === null) throw new Error("window creation failed");
      const firstUrl = canonicalUrl(first?.url) === canonicalUrl(item.url) && String(first?.id || "") === String(item.id || ""); const created = firstUrl ? (await chrome.tabs.query({ windowId })).find(tab => canonicalUrl(tab.url) === itemUrlKey) : await chrome.tabs.create({ windowId, url: item.url, index: Math.max(0, Number(item.tabIndex) || 0), pinned: !!item.pinned, active: false });
      if (!created) throw new Error("tab creation failed");
      let restorationWarning = "";
      if (item.pinned && !created.pinned) { try { await chrome.tabs.update(created.id, { pinned: true }); } catch { restorationWarning = "Tab opened, but pinning failed."; } }
      if (Number.isInteger(Number(item.tabIndex)) && Number(item.tabIndex) > 0) { try { await chrome.tabs.move(created.id, { index: Number(item.tabIndex) }); } catch { restorationWarning ||= "Tab opened, but saved order could not be restored."; } }
      if (!openedByWindow.has(windowKey)) openedByWindow.set(windowKey, new Set()); openedByWindow.get(windowKey).add(itemUrlKey);
      if (item.active) activeByWindow.set(windowKey, created.id);
      if (created.id !== undefined && created.id !== null && (item.browserTabGroupId || item.browserTabGroupTitle)) { const key = `${windowId}:${item.browserTabGroupId || item.browserTabGroupTitle}`; if (!grouped.has(key)) grouped.set(key, { tabIds: [], itemKeys: [] }); grouped.get(key).tabIds.push(created.id); grouped.get(key).itemKeys.push(item.id || item.url); }
      results.push({ itemKey: item.id || item.url, status: restorationWarning ? "Failed" : "Opened", message: restorationWarning || "Tab created" });
    } catch { results.push({ itemKey: item.id || item.url, status: "Failed", message: "Browser could not create this tab" }); }
    await yieldAfterBatch();
    }
  }
  for (const [key, group] of grouped) { try { const groupId = await chrome.tabs.group({ tabIds: group.tabIds }); const title = savedTabs.find(tab => `${windows.get(tab.browserWindowGroupId)}:${tab.browserTabGroupId || tab.browserTabGroupTitle}` === key); if (title?.browserTabGroupTitle) await chrome.tabGroups.update(groupId, { title: title.browserTabGroupTitle, color: title.browserTabGroupColor || "grey" }); } catch { for (const itemKey of group.itemKeys) { const result = results.find(candidate => candidate.itemKey === itemKey && candidate.status === "Opened"); if (result) { result.status = "Failed"; result.message = "Tab opened, but its saved tab-group could not be restored."; } } } }
  for (const [windowKey, tabId] of activeByWindow) { try { await chrome.tabs.update(tabId, { active: true }); } catch { } }
  return { status: "OK", results };
}
async function closeTabs(items) {
  const results = []; let processed = 0; const yieldAfterBatch = async () => { processed += 1; if (processed % OPERATION_BATCH_SIZE === 0) await new Promise(resolve => setTimeout(resolve, 0)); };
  for (const item of items.slice(0, MAX_TABS)) {
    const id = Number(item?.sessionTabId); if (!Number.isInteger(id) || id < 0) { results.push({ itemKey: item?.id || "unknown", status: "Stale", message: "The tab identity is no longer valid" }); await yieldAfterBatch(); continue; }
    let tab; try { tab = await chrome.tabs.get(id); } catch { results.push({ itemKey: item?.id || item?.sessionTabId || "unknown", status: "Stale", message: "The captured tab no longer exists and was not closed by WorkParcel" }); await yieldAfterBatch(); continue; }
    if (!isRestorable(tab.url || "")) { results.push({ itemKey: item.id || item.sessionTabId, status: "Unsupported", message: "Browser-internal and non-HTTP tabs are never closed by WorkParcel" }); await yieldAfterBatch(); continue; }
    if (state.browser !== item.browser || !item.connectionId || item.connectionId !== state.connectionId || !item.expectedUrl || tab.incognito || String(tab.windowId) !== String(item.sessionWindowId) || tab.url !== item.expectedUrl) { results.push({ itemKey: item.id || item.sessionTabId, status: "Stale", message: "The tab no longer matches the captured browser, connection, window or URL" }); await yieldAfterBatch(); continue; }
    try { await chrome.tabs.remove(id); results.push({ itemKey: item.id || item.sessionTabId, status: "Closed", message: "Close request accepted" }); } catch { results.push({ itemKey: item?.id || item?.sessionTabId || "unknown", status: "StillOpen", message: "The tab could not be closed after identity verification" }); }
    await yieldAfterBatch();
  }
  return { status: "OK", results };
}

let snapshotTimer = null; function scheduleSnapshot() { if (snapshotTimer) return; snapshotTimer = setTimeout(() => { snapshotTimer = null; sendSnapshot(); }, 250); }
chrome.tabs.onCreated.addListener(scheduleSnapshot); chrome.tabs.onRemoved.addListener(scheduleSnapshot); chrome.tabs.onUpdated.addListener(scheduleSnapshot); chrome.windows.onCreated.addListener(scheduleSnapshot); chrome.windows.onRemoved.addListener(scheduleSnapshot); chrome.runtime.onStartup.addListener(() => { connect(); scheduleSnapshot(); }); chrome.runtime.onInstalled.addListener(() => { connect(); scheduleSnapshot(); }); try { chrome.storage.local.get(["workParcelConnection", "workParcelHandledRequests"]).then(saved => { if (saved?.workParcelConnection) state = { ...state, ...saved.workParcelConnection, connectionId: null }; if (Array.isArray(saved?.workParcelHandledRequests)) for (const entry of saved.workParcelHandledRequests.slice(-100)) if (entry && typeof entry.id === "string" && Number(entry.expiresAt) > Date.now()) persistedHandledRequests.set(entry.id, Number(entry.expiresAt)); connect(); }).catch(() => connect()); } catch { connect(); }
chrome.tabGroups?.onCreated?.addListener?.(scheduleSnapshot); chrome.tabGroups?.onUpdated?.addListener?.(scheduleSnapshot); chrome.tabGroups?.onRemoved?.addListener?.(scheduleSnapshot);
chrome.runtime.onMessage.addListener((message, sender, respond) => { if (!message || typeof message.type !== "string") { respond({ ok: false, error: "Invalid request" }); return false; } if (message.type === "status") { respond({ ok: true, state }); return false; } if (message.type === "open") { sendToApp("open_workparcel").then(result => respond({ ok: true, result })).catch(error => respond({ ok: false, error: String(error) })); return true; } if (message.type === "refresh") { sendToApp("refresh_tabs").then(result => respond({ ok: true, result })).catch(error => respond({ ok: false, error: String(error) })); return true; } if (message.type === "list") { sendToApp("list_tabs").then(result => respond({ ok: true, result })).catch(error => respond({ ok: false, error: String(error) })); return true; } return false; });
