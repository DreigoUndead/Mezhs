// Builds the Chrome-facing identity shared by provider windows and OAuth child windows.
function createChromeRuntime() {
  return {
    app: {
      isInstalled: false,
      InstallState: {
        DISABLED: "disabled",
        INSTALLED: "installed",
        NOT_INSTALLED: "not_installed"
      },
      RunningState: {
        CANNOT_RUN: "cannot_run",
        READY_TO_RUN: "ready_to_run",
        RUNNING: "running"
      },
      getDetails() { return null; },
      getIsInstalled() { return false; },
      runningState() { return "cannot_run"; }
    },
    csi() {
      return {
        onloadT: Date.now(),
        startE: Date.now(),
        pageT: globalThis.performance?.now?.() ?? 0,
        tran: 15
      };
    },
    loadTimes() {
      const timing = globalThis.performance?.timing || {};
      const seconds = value => (value || Date.now()) / 1000;
      return {
        requestTime: seconds(timing.navigationStart),
        startLoadTime: seconds(timing.navigationStart),
        commitLoadTime: seconds(timing.responseStart),
        finishDocumentLoadTime: seconds(timing.domContentLoadedEventEnd),
        finishLoadTime: seconds(timing.loadEventEnd),
        firstPaintTime: seconds(timing.responseStart),
        firstPaintAfterLoadTime: 0,
        navigationType: "Other",
        wasFetchedViaSpdy: true,
        wasNpnNegotiated: true,
        npnNegotiatedProtocol: "h2",
        wasAlternateProtocolAvailable: false,
        connectionInfo: "h2"
      };
    }
  };
}

function cleanChromeUserAgent(userAgent) {
  return String(userAgent || "")
    .replace(/\sElectron\/[^\s]+/g, "")
    .replace(/\smezhs[^\s]*/gi, "")
    .trim();
}

function createClientHints(chromeVersion, platform, systemVersion, arch) {
  const fullVersion = String(chromeVersion || "");
  const majorVersion = fullVersion.split(".")[0];
  if (!majorVersion)
    throw new Error("Chromium version is unavailable.");

  const platformName = platform === "darwin"
    ? '"macOS"'
    : platform === "win32"
      ? '"Windows"'
      : '"Linux"';
  const architecture = arch === "arm64" ? '"arm"' : '"x86"';

  return {
    "Sec-CH-UA": `"Chromium";v="${majorVersion}", "Google Chrome";v="${majorVersion}", "Not?A_Brand";v="99"`,
    "Sec-CH-UA-Mobile": "?0",
    "Sec-CH-UA-Platform": platformName,
    "Sec-CH-UA-Platform-Version": `"${systemVersion}"`,
    "Sec-CH-UA-Arch": architecture,
    "Sec-CH-UA-Bitness": '"64"',
    "Sec-CH-UA-Full-Version": `"${fullVersion}"`,
    "Sec-CH-UA-Full-Version-List": `"Chromium";v="${fullVersion}", "Google Chrome";v="${fullVersion}", "Not?A_Brand";v="99.0.0.0"`,
    "Sec-CH-UA-Model": '""',
    "Sec-CH-UA-WoW64": "?0",
    "Sec-CH-UA-Form-Factors": '"Desktop"'
  };
}

function configureSessionBrowserIdentity(browserSession, runtime = process) {
  const userAgent = cleanChromeUserAgent(browserSession.getUserAgent());
  const clientHints = createClientHints(
    runtime.versions.chrome,
    runtime.platform,
    runtime.getSystemVersion(),
    runtime.arch);

  browserSession.setUserAgent(userAgent);
  browserSession.webRequest.onBeforeSendHeaders(
    { urls: ["https://*/*"] },
    (details, callback) => {
      Object.assign(details.requestHeaders, clientHints);
      callback({ requestHeaders: details.requestHeaders });
    });

  return { userAgent, clientHints };
}

module.exports = {
  createChromeRuntime,
  cleanChromeUserAgent,
  createClientHints,
  configureSessionBrowserIdentity
};
