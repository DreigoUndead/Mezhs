"use strict";

const { inspectRspackExportsPage } = require("./chatgpt-rspack-diagnostics");

// Execute an ordinary function in the renderer's main world, not Electron's
// isolated preload world. Arguments are serialized as data, never source.
function executePageFunction(webContents, pageFunction, ...args) {
  const source = "(" + pageFunction.toString() + ")(" +
    args.map(value => JSON.stringify(value)).join(",") + ")";
  return webContents.executeJavaScript(source, true);
}

function inspectDocumentPage(point = null) {
  const clean = value => String(value ?? "").replace(/\s+/g, " ").trim().slice(0, 240);
  const visible = element => {
    const rect = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    return rect.width > 0 && rect.height > 0 &&
      style.visibility !== "hidden" && style.display !== "none" &&
      Number(style.opacity || 1) !== 0;
  };
  const describe = element => {
    if (!element) return null;
    const rect = element.getBoundingClientRect();
    return {
      tag: element.tagName?.toLowerCase() || null,
      role: element.getAttribute?.("role") || null,
      ariaLabel: clean(element.getAttribute?.("aria-label")),
      testId: clean(element.getAttribute?.("data-testid")),
      name: clean(element.getAttribute?.("name")),
      type: clean(element.getAttribute?.("type")),
      text: element.type === "password" ? "" :
        clean(element.innerText || element.textContent || element.value),
      disabled: Boolean(element.disabled || element.getAttribute?.("aria-disabled") === "true"),
      expanded: element.getAttribute?.("aria-expanded") || null,
      checked: element.getAttribute?.("aria-checked") || null,
      selected: element.getAttribute?.("aria-selected") || null,
      x: Math.round(rect.x),
      y: Math.round(rect.y),
      width: Math.round(rect.width),
      height: Math.round(rect.height)
    };
  };

  if (point)
    return describe(document.elementFromPoint(point.x, point.y));

  const selector = [
    "button", "a[href]", "input", "textarea", "select",
    "[role]", '[contenteditable="true"]', "[data-testid]", "[aria-label]"
  ].join(",");
  return {
    url: location.origin + location.pathname,
    title: document.title,
    viewport: { width: innerWidth, height: innerHeight },
    activeElement: describe(document.activeElement),
    elements: [...document.querySelectorAll(selector)]
      .filter(visible)
      .slice(0, 300)
      .map(describe)
  };
}

async function captureDiagnosticNetwork({ window, sleep }, action, waitMs, { traceHeaders = false } = {}) {
  const debuggerClient = window.webContents.debugger;
  const alreadyAttached = debuggerClient.isAttached();
  const requests = [];
  const responses = new Map();
  const additionalHeaders = new Map();
  const listener = (_event, method, params) => {
    if (method === "Network.requestWillBeSent") {
      try {
        const parsed = new URL(params.request.url);
        if (traceHeaders && (
          parsed.origin !== "https://chatgpt.com" ||
          !parsed.pathname.startsWith("/backend-api/")
        )) return;
        requests.push({
          id: params.requestId,
          method: params.request.method,
          url: parsed.origin + parsed.pathname,
          resourceType: params.type || null,
          ...(traceHeaders ? {
            // Never expose header values, credentials, query strings or request bodies.
            headerNames: Object.keys(params.request.headers || {})
          } : {})
        });
      } catch {
        if (!traceHeaders) {
          requests.push({
            id: params.requestId,
            method: params.request.method,
            url: null,
            resourceType: params.type || null
          });
        }
      }
    } else if (traceHeaders && method === "Network.requestWillBeSentExtraInfo") {
      additionalHeaders.set(params.requestId, Object.keys(params.headers || {}));
    } else if (method === "Network.responseReceived") {
      responses.set(params.requestId, params.response.status);
    } else if (traceHeaders && method === "Network.responseReceivedExtraInfo") {
      if (!responses.has(params.requestId))
        responses.set(params.requestId, params.statusCode);
    }
  };

  if (!alreadyAttached) debuggerClient.attach("1.3");
  debuggerClient.on("message", listener);
  try {
    await debuggerClient.sendCommand("Network.enable");
    await action();
    if (waitMs > 0) await sleep(waitMs);
    return requests.map(request => ({
      ...request,
      ...(traceHeaders ? {
        headerNames: [...new Set([
          ...(request.headerNames || []),
          ...(additionalHeaders.get(request.id) || [])
        ])].sort()
      } : {}),
      status: responses.get(request.id) ?? null
    }));
  } finally {
    debuggerClient.removeListener("message", listener);
    if (!alreadyAttached && debuggerClient.isAttached())
      debuggerClient.detach();
  }
}

function numericPoint(args) {
  const x = Number(args?.x);
  const y = Number(args?.y);
  if (!Number.isFinite(x) || !Number.isFinite(y))
    throw new Error("Diagnostic operation requires finite x and y coordinates.");
  return { x, y };
}

function boundedWait(value, defaultMs, minimumMs, maximumMs) {
  const ms = Number(value ?? defaultMs);
  if (!Number.isFinite(ms))
    throw new Error("Diagnostic waitMs must be a finite number.");
  return Math.max(minimumMs, Math.min(ms, maximumMs));
}

async function invokeDiagnostic(context, operation, args = {}) {
  const { window, browserModule, sleep } = context;
  switch (operation) {
    case "inspectRspackExports": {
      if (browserModule?.name !== "ChatGPT")
        throw new Error("Rspack export inspection is limited to ChatGPT diagnostics.");
      const moduleId = String(args?.moduleId || "").trim();
      const keys = args?.exportKeys;
      if (!/^[A-Za-z0-9_$-]{1,32}$/.test(moduleId) ||
          !Array.isArray(keys) || keys.length < 1 || keys.length > 4 ||
          !keys.every(key => typeof key === "string" &&
            /^[A-Za-z0-9_$-]{1,32}$/.test(key)))
        throw new Error("Invalid Rspack module id or export keys.");
      return executePageFunction(window.webContents, inspectRspackExportsPage, moduleId, keys);
    }
    case "networkTrace": {
      const waitMs = boundedWait(args?.waitMs, 30000, 1000, 60000);
      return captureDiagnosticNetwork(context, () => {
        window.show();
        window.focus();
      }, waitMs, { traceHeaders: true });
    }
    case "snapshot":
      return executePageFunction(window.webContents, inspectDocumentPage);
    case "inspectPoint":
      return executePageFunction(window.webContents, inspectDocumentPage, numericPoint(args));
    case "click": {
      const { x, y } = numericPoint(args);
      const waitMs = boundedWait(args?.waitMs, 750, 0, 10000);
      const before = await executePageFunction(window.webContents, inspectDocumentPage);
      const network = await captureDiagnosticNetwork(context, async () => {
        window.webContents.sendInputEvent({ type: "mouseMove", x, y });
        window.webContents.sendInputEvent({ type: "mouseDown", x, y, button: "left", clickCount: 1 });
        window.webContents.sendInputEvent({ type: "mouseUp", x, y, button: "left", clickCount: 1 });
      }, waitMs);
      const after = await executePageFunction(window.webContents, inspectDocumentPage);
      return { before, after, network };
    }
    case "type": {
      const text = String(args?.text ?? "");
      await window.webContents.insertText(text);
      return { typed: text.length };
    }
    case "key": {
      const keyCode = String(args?.keyCode || "").trim();
      if (!keyCode) throw new Error("Diagnostic key operation requires keyCode.");
      const modifiers = Array.isArray(args?.modifiers)
        ? args.modifiers.map(value => String(value))
        : [];
      window.webContents.sendInputEvent({ type: "keyDown", keyCode, modifiers });
      window.webContents.sendInputEvent({ type: "keyUp", keyCode, modifiers });
      return { keyCode, modifiers };
    }
    case "screenshot": {
      const image = await window.webContents.capturePage();
      const size = image.getSize();
      return {
        width: size.width,
        height: size.height,
        mimeType: "image/png",
        base64: image.toPNG().toString("base64")
      };
    }
    default:
      throw new Error("Unsupported browser diagnostic operation '" + operation + "'.");
  }
}

module.exports = {
  invokeDiagnostic,
  executePageFunction,
  inspectDocumentPage,
  captureDiagnosticNetwork
};
