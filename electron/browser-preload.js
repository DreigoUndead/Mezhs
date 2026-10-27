// Attaches provider page operations to each Electron renderer. The Chrome runtime
// shim below is the isolated Electron-34 compatibility exception for Google OAuth.
const { ipcRenderer, webFrame } = require("electron");
if (process.sandboxed !== true)
  throw new Error("MEZS provider preload requires an enabled renderer sandbox.");
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

ipcRenderer.on("mezhs:page-operation", async (_event, request) => {
  const responseChannel = String(request?.responseChannel || "");
  if (!responseChannel) return;

  try {
    const operation = String(request?.operation || "");
    const method = Object.hasOwn(pageOperations, operation) ? pageOperations[operation] : null;
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
