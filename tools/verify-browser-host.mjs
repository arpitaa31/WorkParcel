import net from "node:net";
import { spawn } from "node:child_process";
import { once } from "node:events";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const executable = path.join(root, "src", "WorkParcel.BrowserHost", "bin", "Debug", "net10.0", "WorkParcel.BrowserHost.exe");
const pipeName = "\\\\.\\pipe\\WorkParcel.Browser.v1";
const appMessage = {
  version: 1,
  requestId: "host-no-origin",
  type: "connection_status",
  browser: "chrome",
  connectionId: "fake-app-connection",
  extensionVersion: "0.2.0",
  timestampUtc: new Date().toISOString(),
  payload: { status: "CONNECTED" }
};
const futureBrowserMessage = {
  ...appMessage,
  version: 99,
  requestId: "future-protocol"
};

const frame = value => {
  const body = Buffer.from(JSON.stringify(value), "utf8");
  const header = Buffer.alloc(4);
  header.writeUInt32LE(body.length, 0);
  return Buffer.concat([header, body]);
};

const readFrame = onMessage => {
  let buffer = Buffer.alloc(0);
  return chunk => {
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 4) {
      const length = buffer.readUInt32LE(0);
      if (buffer.length < length + 4) return;
      const body = buffer.subarray(4, length + 4);
      buffer = buffer.subarray(length + 4);
      onMessage(JSON.parse(body.toString("utf8")));
    }
  };
};

let relayedBrowserMessage;
const server = net.createServer(socket => {
  socket.on("error", () => {});
  socket.on("data", readFrame(message => { relayedBrowserMessage = message; }));
  setTimeout(() => socket.write(frame(appMessage)), 30);
});
server.listen(pipeName);
await once(server, "listening");

const host = spawn(executable, [], { cwd: root, stdio: ["pipe", "pipe", "pipe"], env: { ...process.env, WORKPARCEL_BROWSER_HOST_SMOKE: "1" } });
let response;
const onStdout = readFrame(message => { response = message; });
host.stdout.on("data", onStdout);
host.stderr.on("data", () => {});
host.stdin.write(frame(futureBrowserMessage));

const deadline = Date.now() + 2500;
while ((!response || !relayedBrowserMessage) && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 25));
if (!response || response.requestId !== appMessage.requestId || response.type !== appMessage.type || !relayedBrowserMessage || relayedBrowserMessage.version !== futureBrowserMessage.version || relayedBrowserMessage.requestId !== futureBrowserMessage.requestId) {
  host.kill();
  server.close();
  throw new Error("Native host did not relay valid app-pipe frames when launched without an origin argument.");
}

host.kill();
await once(host, "exit").catch(() => {});
server.close();
await once(server, "close").catch(() => {});
const productionHost = spawn(executable, [], { cwd: root, stdio: ["ignore", "pipe", "pipe"], env: (() => { const value = { ...process.env }; delete value.WORKPARCEL_BROWSER_HOST_SMOKE; return value; })() });
const productionExit = Promise.race([
  once(productionHost, "exit").then(() => true),
  new Promise(resolve => setTimeout(() => resolve(false), 1500))
]);
if (!(await productionExit)) { productionHost.kill(); throw new Error("Native host accepted a production launch without an extension origin."); }
console.log("native host framing and origin-gate smoke passed: diagnostic relay works and production no-origin launch is rejected");
