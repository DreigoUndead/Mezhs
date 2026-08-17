const { app, BrowserWindow, session } = require("electron");
const http = require("node:http");
const path = require("node:path");
const {
  configureSessionBrowserIdentity
} = require("./browser-identity");

let window = null;
let browserModule = null;
let activeSession = null;
let keepVisible = false;
let shuttingDown = false;
const parentProcessId = Number(process.env.MEZHS_PARENT_PROCESS_ID || 0);
const browserIdentityPreload = path.join(__dirname, "browser-identity-preload.js");

app.commandLine.appendSwitch("no-sandbox");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("disable-gpu-compositing");
app.commandLine.appendSwitch("disable-software-rasterizer");
app.commandLine.appendSwitch("disable-background-timer-throttling");
app.commandLine.appendSwitch("disable-backgrounding-occluded-windows");
app.commandLine.appendSwitch("disable-renderer-backgrounding");
app.disableHardwareAcceleration();

if (parentProcessId > 0) {
  setInterval(() => {
    try {
      process.kill(parentProcessId, 0);
    } catch {
      shuttingDown = true;
      app.quit();
    }
  }, 2000).unref();
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function loadBrowserModule(modulePath) {
  if (!modulePath)
    throw new Error("Browser module path is required.");
  const resolved = path.resolve(modulePath);
  const implementation = require(resolved);
  if (!implementation?.homeUrl || !implementation.operations)
    throw new Error(`Browser module '${resolved}' is incomplete.`);
  return implementation;
}

async function initialize({ profileDirectory, showBrowser, modulePath, requireAuthorization }) {
  await app.whenReady();
  browserModule = loadBrowserModule(modulePath);
  keepVisible = Boolean(showBrowser);
  const persistentSession = session.fromPath(path.resolve(profileDirectory));
  configureSessionBrowserIdentity(persistentSession);
  activeSession = persistentSession;

  console.error(
    `Initializing ${browserModule.name} window ` +
    `(visible=${keepVisible}, authorization=${Boolean(requireAuthorization)}, profile=${profileDirectory})`
  );
  window = new BrowserWindow({
    width: 1200,
    height: 850,
    show: keepVisible,
    title: `MEŽS - ${browserModule.name}`,
    webPreferences: {
      session: persistentSession,
      preload: browserIdentityPreload,
      contextIsolation: true,
      sandbox: false,
      backgroundThrottling: false
    }
  });
  window.webContents.setWindowOpenHandler(() => ({
    action: "allow",
    overrideBrowserWindowOptions: {
      webPreferences: {
        preload: browserIdentityPreload,
        contextIsolation: true,
        sandbox: false
      }
    }
  }));

  window.on("close", event => {
    if (shuttingDown) return;
    event.preventDefault();
    window.hide();
  });
  window.webContents.on("did-fail-load", (_event, code, description, url) => {
    console.error(`Navigation failed (${code} ${description}): ${url}`);
  });
  window.webContents.on("console-message", (_event, level, message, line, sourceId) => {
    if (level >= 2)
      console.error(`${browserModule.name} renderer: ${message} (${sourceId}:${line})`);
  });
  window.once("ready-to-show", () => {
    if (keepVisible) {
      window.show();
      window.focus();
    }
  });

  await window.loadURL(browserModule.homeUrl);
  console.error(`${browserModule.name} navigation completed at ${window.webContents.getURL()}`);

  if (requireAuthorization) {
    if (typeof browserModule.isAuthorized !== "function")
      throw new Error(`Browser module '${modulePath}' does not support authorization.`);
    if (!await browserModule.isAuthorized(window) && !keepVisible) {
      const error = new Error(`${browserModule.name} authorization is required.`);
      error.code = "authorization_required";
      throw error;
    }
    while (!await browserModule.isAuthorized(window))
      await sleep(1000);
    await persistentSession.flushStorageData();
    await persistentSession.cookies.flushStore();
    console.error(`${browserModule.name} authorization confirmed and persisted.`);
  }

  if (typeof browserModule.afterInitialize === "function")
    await browserModule.afterInitialize({ window, session: persistentSession, sleep });
  if (!keepVisible) window.hide();
  return { ready: true };
}

function invokeProvider({ operation, arguments: args }) {
  if (!window || !browserModule || !activeSession)
    throw new Error("Electron browser is not initialized.");
  const method = browserModule.operations[operation];
  if (typeof method !== "function")
    throw new Error(`${browserModule.name} does not support provider operation '${operation}'.`);
  return method({ window, session: activeSession, args: args ?? {}, sleep });
}

function readJson(request) {
  return new Promise((resolve, reject) => {
    let body = "";
    request.setEncoding("utf8");
    request.on("data", chunk => body += chunk);
    request.on("end", () => {
      try { resolve(body ? JSON.parse(body) : {}); }
      catch (error) { reject(error); }
    });
    request.on("error", reject);
  });
}

function writeJson(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body)
  });
  response.end(body);
}

let operationQueue = Promise.resolve();

async function start() {
  const server = http.createServer(async (request, response) => {
    try {
      if (request.method === "POST" && request.url === "/invoke") {
        const body = await readJson(request);
        const result = await (operationQueue = operationQueue
          .catch(() => {})
          .then(() => invokeProvider(body)));
        writeJson(response, 200, result);
      } else if (request.method === "POST" && request.url === "/show") {
        window?.show();
        window?.focus();
        writeJson(response, 200, { shown: true });
      } else if (request.method === "POST" && request.url === "/shutdown") {
        shuttingDown = true;
        activeSession?.flushStorageData();
        await activeSession?.cookies.flushStore();
        writeJson(response, 200, { stopped: true });
        server.close(() => app.quit());
      } else {
        writeJson(response, 404, { error: "Not found" });
      }
    } catch (error) {
      writeJson(response, 500, { ok: false, error: String(error?.stack ?? error) });
    }
  });

  server.listen(0, "127.0.0.1", async () => {
    try {
      await initialize({
        profileDirectory: process.env.MEZHS_PROFILE_DIRECTORY,
        showBrowser: process.env.MEZHS_SHOW_BROWSER === "1",
        modulePath: process.env.MEZHS_BROWSER_MODULE,
        requireAuthorization: process.env.MEZHS_REQUIRE_AUTHORIZATION === "1"
      });
      const address = server.address();
      process.stdout.write(`${JSON.stringify({ event: "ready", port: address.port })}\n`);
    } catch (error) {
      process.stdout.write(`${JSON.stringify({
        event: "error",
        code: error?.code || null,
        error: String(error?.stack ?? error)
      })}\n`);
      server.close(() => app.quit());
    }
  });
}

start();

app.on("window-all-closed", event => {
  if (!shuttingDown) event.preventDefault();
});
