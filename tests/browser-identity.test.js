const assert = require("node:assert/strict");
const test = require("node:test");
const {
  cleanChromeUserAgent,
  configureSessionBrowserIdentity,
  installChromeRuntime
} = require("../electron/browser-identity");

test("Chrome user agent removes Electron and Mezhs product tokens", () => {
  const userAgent = cleanChromeUserAgent(
    "Mozilla/5.0 Chrome/132.0.6834.210 Safari/537.36 Electron/34.5.8 mezhs-browser-electron-sidecar/0.1.0");

  assert.equal(
    userAgent,
    "Mozilla/5.0 Chrome/132.0.6834.210 Safari/537.36");
});

test("session browser identity keeps UA and client hints consistent", () => {
  let sessionUserAgent = null;
  let filter = null;
  let listener = null;
  const browserSession = {
    getUserAgent() {
      return "Mozilla/5.0 Chrome/132.0.6834.210 Safari/537.36 Electron/34.5.8 mezhs/0.1";
    },
    setUserAgent(value) {
      sessionUserAgent = value;
    },
    webRequest: {
      onBeforeSendHeaders(value, callback) {
        filter = value;
        listener = callback;
      }
    }
  };
  const runtime = {
    versions: { chrome: "132.0.6834.210" },
    platform: "win32",
    arch: "x64",
    getSystemVersion() { return "10.0.26100"; }
  };

  const identity = configureSessionBrowserIdentity(browserSession, runtime);

  assert.equal(identity.userAgent, sessionUserAgent);
  assert.match(sessionUserAgent, /Chrome\/132\.0\.6834\.210/);
  assert.doesNotMatch(sessionUserAgent, /Electron|mezhs/i);
  assert.deepEqual(filter, { urls: ["https://*/*"] });

  const details = { requestHeaders: { Existing: "value" } };
  let sentHeaders = null;
  listener(details, result => sentHeaders = result.requestHeaders);

  assert.equal(sentHeaders.Existing, "value");
  assert.match(sentHeaders["Sec-CH-UA"], /"Chromium";v="132"/);
  assert.match(sentHeaders["Sec-CH-UA"], /"Google Chrome";v="132"/);
  assert.equal(sentHeaders["Sec-CH-UA-Platform"], '"Windows"');
  assert.equal(sentHeaders["Sec-CH-UA-Full-Version"], '"132.0.6834.210"');
});

test("Chrome runtime shim is installed once per WebContents", async () => {
  let attached = false;
  const commands = [];
  const webContents = {
    debugger: {
      isAttached() { return attached; },
      attach(version) {
        assert.equal(version, "1.3");
        attached = true;
      },
      async sendCommand(command, argumentsValue) {
        commands.push([command, argumentsValue]);
      }
    }
  };

  const first = installChromeRuntime(webContents);
  const second = installChromeRuntime(webContents);
  assert.equal(first, second);
  assert.equal(await first, true);
  assert.deepEqual(commands.map(([command]) => command), [
    "Page.addScriptToEvaluateOnNewDocument"
  ]);

  const source = commands[0][1].source;
  assert.match(source, /window\.chrome\.app/);
  assert.match(source, /window\.chrome\.csi/);
  assert.match(source, /window\.chrome\.loadTimes/);
});
