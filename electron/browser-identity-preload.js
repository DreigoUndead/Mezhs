const { webFrame } = require("electron");
const { CHROME_RUNTIME_SHIM } = require("./browser-identity");

void webFrame.executeJavaScript(CHROME_RUNTIME_SHIM)
  .catch(error => console.error(
    `Could not install Chrome runtime compatibility: ${error?.message ?? error}`));
