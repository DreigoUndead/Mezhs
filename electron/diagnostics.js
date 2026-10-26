// Diagnostics are separate from provider operations. Page functions remain
// ordinary JavaScript and are serialized only when crossing into the renderer.
function pageScript(fn, ...args) {
  return `(${fn.toString()})(${args.map(arg => JSON.stringify(arg)).join(",")})`;
}

function diagnosticElementSnapshotPage() {
    const clean = value => String(value ?? '').replace(/\s+/g, ' ').trim().slice(0, 240);
    const visible = element => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      return rect.width > 0 && rect.height > 0 &&
        style.visibility !== 'hidden' && style.display !== 'none' &&
        Number(style.opacity || 1) !== 0;
    };
    const describe = element => {
      if (!element) return null;
      const rect = element.getBoundingClientRect();
      return {
        tag: element.tagName?.toLowerCase() || null,
        role: element.getAttribute?.('role') || null,
        ariaLabel: clean(element.getAttribute?.('aria-label')),
        testId: clean(element.getAttribute?.('data-testid')),
        name: clean(element.getAttribute?.('name')),
        type: clean(element.getAttribute?.('type')),
        text: element.type === 'password' ? '' : clean(element.innerText || element.textContent || element.value),
        disabled: Boolean(element.disabled || element.getAttribute?.('aria-disabled') === 'true'),
        expanded: element.getAttribute?.('aria-expanded') || null,
        checked: element.getAttribute?.('aria-checked') || null,
        selected: element.getAttribute?.('aria-selected') || null,
        x: Math.round(rect.x),
        y: Math.round(rect.y),
        width: Math.round(rect.width),
        height: Math.round(rect.height)
      };
    };
    const selector = [
      'button', 'a[href]', 'input', 'textarea', 'select',
      '[role]', '[contenteditable="true"]', '[data-testid]', '[aria-label]'
    ].join(',');
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

function diagnosticElementSnapshot() {
  return pageScript(diagnosticElementSnapshotPage);
}

async function captureDiagnosticNetwork(action, waitMs, { traceHeaders = false, window, sleep } = {}) {
  const debuggerClient = window.webContents.debugger;
  const alreadyAttached = debuggerClient.isAttached();
  const requests = [];
  const responses = new Map();
  const additionalHeaders = new Map();
  const listener = (_event, method, params) => {
    if (method === 'Network.requestWillBeSent') {
      try {
        const parsed = new URL(params.request.url);
        if (traceHeaders && (
          parsed.origin !== 'https://chatgpt.com' ||
          !parsed.pathname.startsWith('/backend-api/')
        )) return;
        requests.push({
          id: params.requestId,
          method: params.request.method,
          url: parsed.origin + parsed.pathname,
          resourceType: params.type || null,
          ...(traceHeaders ? {
            // Names only: never record cookies, auth tokens, payloads, or header values.
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
    } else if (traceHeaders && method === 'Network.requestWillBeSentExtraInfo') {
      additionalHeaders.set(params.requestId, Object.keys(params.headers || {}));
    } else if (method === 'Network.responseReceived') {
      responses.set(params.requestId, params.response.status);
    } else if (traceHeaders && method === 'Network.responseReceivedExtraInfo') {
      if (!responses.has(params.requestId))
        responses.set(params.requestId, params.statusCode);
    }
  };

  if (!alreadyAttached) debuggerClient.attach('1.3');
  debuggerClient.on('message', listener);
  try {
    await debuggerClient.sendCommand('Network.enable');
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
    debuggerClient.removeListener('message', listener);
    if (!alreadyAttached && debuggerClient.isAttached())
      debuggerClient.detach();
  }
}


async function inspectRspackExportsPage(moduleId, exportKeys) {
      const resources = performance.getEntriesByType("resource")
        .map(item => String(item.name || ""))
        .filter(url => /^https:\/\/chatgpt\.com\/cdn\/assets\/[^/?#]+\.js(?:[?#].*)?$/i.test(url));
      if (!resources.length)
        throw new Error("ChatGPT Rspack runtime was not loaded.");
      const preferred = resources.filter(url =>
        /\/(?:manifest|entry\.client|root)-[^/?#]*\.js(?:[?#]|$)/i.test(url));
      const candidates = [...new Set([...preferred, ...resources.slice().reverse()])];
      let record;
      for (const runtimeUrl of candidates) {
        try {
          const runtime = await import(runtimeUrl);
          record = runtime?.__webpack_require__?.c?.[moduleId];
          if (record) break;
        } catch {
          // Some loaded scripts are not importable modules; inspect the next asset.
        }
      }
      if (!record)
        throw new Error("Requested Rspack module was not present in the loaded cache.");
      const exports = record.exports;
      const keys = exportKeys;
      const markers = ["chatReq", "turnstileToken", "proofToken"];
      const context = (source, marker) => {
        const index = source.indexOf(marker);
        return index < 0 ? null
          : source.slice(Math.max(0, index - 90), Math.min(source.length, index + marker.length + 130));
      };
      const results = keys.map(key => {
        const value = exports?.[key];
        if (typeof value !== "function")
          return { exportKey: key, type: typeof value };
        const source = Function.prototype.toString.call(value);
        return {
          exportKey: key,
          name: String(value.name || "").slice(0, 64),
          arity: value.length,
          sourceLength: source.length,
          start: source.slice(0, 160),
          end: source.slice(-100),
          markerContexts: Object.fromEntries(markers.map(marker => [marker, context(source, marker)]))
        };
      });
      return {
        moduleId,
        availableExportKeys: Object.keys(exports || {}).slice(0, 30),
        results
      };
}

function inspectRspackExportsScript(moduleId, exportKeys) {
  return pageScript(inspectRspackExportsPage, moduleId, exportKeys);
}

function inspectPointPage(x, y) {
  const element = document.elementFromPoint(x, y);
  if (!element) return null;
  const rect = element.getBoundingClientRect();
  const clean = value => String(value ?? '').replace(/\s+/g, ' ').trim().slice(0, 500);
  return {
    tag: element.tagName?.toLowerCase() || null,
    role: element.getAttribute?.('role') || null,
    ariaLabel: clean(element.getAttribute?.('aria-label')),
    testId: clean(element.getAttribute?.('data-testid')),
    text: element.type === 'password' ? '' : clean(element.innerText || element.textContent || element.value),
    x: Math.round(rect.x),
    y: Math.round(rect.y),
    width: Math.round(rect.width),
    height: Math.round(rect.height)
  };
}

async function invokeDiagnostic(operation, args, { window, browserModule, sleep }) {
  const numericPoint = () => {
    const x = Number(args?.x);
    const y = Number(args?.y);
    if (!Number.isFinite(x) || !Number.isFinite(y))
      throw new Error('Diagnostic operation requires finite x and y coordinates.');
    return { x, y };
  };

  switch (operation) {
    case 'inspectRspackExports': {
      if (browserModule?.name !== 'ChatGPT')
        throw new Error('Rspack export inspection is limited to ChatGPT diagnostics.');
      const moduleId = String(args?.moduleId || '').trim();
      const keys = args?.exportKeys;
      if (!/^[A-Za-z0-9_$-]{1,32}$/.test(moduleId) ||
          !Array.isArray(keys) || keys.length < 1 || keys.length > 4 ||
          !keys.every(key => typeof key === 'string' &&
            /^[A-Za-z0-9_$-]{1,32}$/.test(key)))
        throw new Error('Invalid Rspack module id or export keys.');
      return window.webContents.executeJavaScript(
        inspectRspackExportsScript(moduleId, keys), true
      );
    }
    case 'networkTrace': {
      // Observe a manual browser action. No DOM selectors, request bodies, or secret values.
      const waitMs = Math.max(1000, Math.min(Number(args?.waitMs ?? 30000), 60000));
      return captureDiagnosticNetwork(() => {
        window.show();
        window.focus();
      }, waitMs, { traceHeaders: true, window, sleep });
    }

    case 'snapshot':
      return window.webContents.executeJavaScript(diagnosticElementSnapshot());

    case 'inspectPoint': {
      const { x, y } = numericPoint();
      return window.webContents.executeJavaScript(pageScript(inspectPointPage, x, y));
    }

    case 'click': {
      const { x, y } = numericPoint();
      const waitMs = Math.max(0, Math.min(Number(args?.waitMs ?? 750), 10000));
      const before = await window.webContents.executeJavaScript(diagnosticElementSnapshot());
      const network = await captureDiagnosticNetwork(async () => {
        window.webContents.sendInputEvent({ type: 'mouseMove', x, y });
        window.webContents.sendInputEvent({ type: 'mouseDown', x, y, button: 'left', clickCount: 1 });
        window.webContents.sendInputEvent({ type: 'mouseUp', x, y, button: 'left', clickCount: 1 });
      }, waitMs, { window, sleep });
      const after = await window.webContents.executeJavaScript(diagnosticElementSnapshot());
      return { before, after, network };
    }

    case 'type': {
      const text = String(args?.text ?? '');
      await window.webContents.insertText(text);
      return { typed: text.length };
    }

    case 'key': {
      const keyCode = String(args?.keyCode || '').trim();
      if (!keyCode) throw new Error('Diagnostic key operation requires keyCode.');
      const modifiers = Array.isArray(args?.modifiers)
        ? args.modifiers.map(value => String(value))
        : [];
      window.webContents.sendInputEvent({ type: 'keyDown', keyCode, modifiers });
      window.webContents.sendInputEvent({ type: 'keyUp', keyCode, modifiers });
      return { keyCode, modifiers };
    }

    case 'screenshot': {
      const image = await window.webContents.capturePage();
      const size = image.getSize();
      return {
        width: size.width,
        height: size.height,
        mimeType: 'image/png',
        base64: image.toPNG().toString('base64')
      };
    }

    default:
      throw new Error(`Unsupported browser diagnostic operation '${operation}'.`);
  }
}

module.exports = {
  invokeDiagnostic,
  captureDiagnosticNetwork,
  diagnosticElementSnapshot,
  inspectRspackExportsScript
};
