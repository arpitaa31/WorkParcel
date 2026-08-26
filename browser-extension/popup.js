const $ = selector => document.querySelector(selector);
function render(state = {}) {
  const status = state.status || "CONNECTION ERROR";
  $("#status").textContent = status;
  $("#status").dataset.state = status.toLowerCase().replaceAll(" ", "-");
  const browser = (state.browser || "browser").toUpperCase();
  $("#details").textContent = `${browser} · ${state.lastConnected ? `last connected ${new Date(state.lastConnected).toLocaleTimeString()}` : "not connected"}`;
  $("#counts").textContent = `${Number(state.windowCount || 0)} WINDOWS · ${Number(state.tabCount || 0)} TABS`;
}
async function refreshStatus() {
  try { const response = await chrome.runtime.sendMessage({ type: "status" }); render(response?.state); }
  catch { render({ status: "CONNECTION ERROR" }); }
}
$("#open").addEventListener("click", async () => {
  try { const response = await chrome.runtime.sendMessage({ type: "open" }); $("#result").textContent = response?.ok ? "WorkParcel is running." : response?.error || "WorkParcel is not reachable."; }
  catch (error) { $("#result").textContent = String(error); }
});
$("#refresh").addEventListener("click", async () => {
  $("#status").textContent = "CONNECTING";
  try { await chrome.runtime.sendMessage({ type: "refresh" }); $("#result").textContent = "Refresh requested from WorkParcel."; }
  catch (error) { $("#result").textContent = String(error); }
  await refreshStatus();
});
refreshStatus();
setInterval(refreshStatus, 3000);
