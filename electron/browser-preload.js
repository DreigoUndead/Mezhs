// Attaches provider page operations to each Electron renderer and exposes the Chrome runtime surface.
const { contextBridge, ipcRenderer } = require("electron");
const path = require("node:path");
const { createChromeRuntime } = require("./browser-identity");

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

try {
  contextBridge.exposeInMainWorld("chrome", createChromeRuntime());
} catch (error) {
  console.error(
    `Could not expose Chrome runtime compatibility: ${error?.message ?? error}`);
}
