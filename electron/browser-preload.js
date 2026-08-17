// Attaches provider page operations to each Electron renderer. The Chrome runtime
// shim below is the isolated Electron-34 compatibility exception for Google OAuth.
const { ipcRenderer, webFrame } = require("electron");
const path = require("node:path");
const { CHROME_RUNTIME_SHIM } = require("./browser-identity");

const modulePath = process.env.MEZHS_BROWSER_MODULE;
const browserModule = modulePath
  ? require(path.resolve(modulePath))
  : null;
const pageOperations = browserModule?.pageOperations || {};
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

ipcRenderer.on("mezhs:page-operation", async (_event, request) => {
  const responseChannel = String(request?.responseChannel || "");
  if (!responseChannel) return;

  try {
    const operation = String(request?.operation || "");
    const method = pageOperations[operation];
    if (typeof method !== "function")
      throw new Error(`Browser page operation '${operation}' is not available.`);

    const result = await method({
      args: request?.args ?? {},
      sleep
    });
    ipcRenderer.send(responseChannel, { ok: true, result });
  } catch (error) {
    ipcRenderer.send(responseChannel, {
      ok: false,
      error: String(error?.stack ?? error)
    });
  }
});

void webFrame.executeJavaScript(CHROME_RUNTIME_SHIM).catch(error => {
  console.error(
    `Could not install Chrome runtime compatibility: ${error?.message ?? error}`);
});
