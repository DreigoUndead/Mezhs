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


function rspackTimingPage(clear) {
  const count = () => {
    const entries = performance.getEntriesByType("resource");
    return {
      resourceEntries: entries.length,
      runtimeAssets: entries.filter(entry =>
        /^https:\/\/chatgpt\.com\/cdn\/assets\/[^/?#]+\.js(?:[?#].*)?$/i.test(
          String(entry.name || ""))).length
    };
  };
  const before = count();
  if (clear) performance.clearResourceTimings();
  return { cleared: clear, before, after: count() };
}

function rspackRegistryShapePage(root = globalThis) {
  // Read-only shape classification. Proxy descriptor traps may still run page code.
  const empty = () => ({
    scannedGlobals: 0,
    matchingNames: 0,
    dataArrayCandidates: 0,
    accessorCandidates: 0,
    ownPushFunctions: 0,
    inheritedOrAbsentPush: 0,
    nativeLookingPush: 0,
    nonNativePush: 0,
    emptyArrays: 0,
    processedEntryShapeCandidates: 0,
    noProcessedEntryShape: 0,
    passedNameAndArray: 0,
    passedNameArrayAndOwnPush: 0,
    passedNameArrayOwnPushNonNative: 0,
    passedAllPredicates: 0,
    descriptorErrors: 0,
    limitExceeded: false,
    incomplete: false
  });
  const failure = limit => {
    // Do not publish a partially successful aggregate after an unsafe scan.
    const result = empty();
    result.incomplete = true;
    if (limit) result.limitExceeded = true;
    else result.descriptorErrors = 1;
    return result;
  };
  try {
    const result = empty();
    const own = (object, key) => {
      if (object === null || (typeof object !== 'object' && typeof object !== 'function'))
        return { kind: 'missing' };
      const descriptor = Object.getOwnPropertyDescriptor(object, key);
      if (!descriptor) return { kind: 'missing' };
      return Object.prototype.hasOwnProperty.call(descriptor, 'value')
        ? { kind: 'data', value: descriptor.value }
        : { kind: 'accessor' };
    };
    const names = Object.getOwnPropertyNames(root);
    if (names.length > 5000) return failure(true);
    result.scannedGlobals = names.length;
    let inspectedEntries = 0;
    for (const name of names) {
      if (!/^webpackChunk[A-Za-z0-9_$]{1,80}$/.test(name)) continue;
      result.matchingNames++;
      if (result.matchingNames > 32) return failure(true);
      let sawAccessor = false;
      const read = (object, key) => {
        const descriptor = own(object, key);
        if (descriptor.kind === 'accessor' && !sawAccessor) {
          sawAccessor = true;
          result.accessorCandidates++;
        }
        return descriptor.kind === 'data' ? descriptor.value : undefined;
      };
      const registry = read(root, name);
      if (!Array.isArray(registry)) continue;
      result.dataArrayCandidates++;
      result.passedNameAndArray++;
      const push = read(registry, 'push');
      if (typeof push !== 'function') {
        result.inheritedOrAbsentPush++;
        continue;
      }
      result.ownPushFunctions++;
      result.passedNameArrayAndOwnPush++;
      if (push === Array.prototype.push ||
          /\[native code\]/.test(Function.prototype.toString.call(push))) {
        result.nativeLookingPush++;
        continue;
      }
      result.nonNativePush++;
      result.passedNameArrayOwnPushNonNative++;
      const length = read(registry, 'length');
      if (length === 0) result.emptyArrays++;
      if (Number.isSafeInteger(length) && length > 2048) return failure(true);
      if (!Number.isSafeInteger(length) || length < 1) {
        result.noProcessedEntryShape++;
        continue;
      }
      let processed = false;
      for (let index = 0; index < length; index++) {
        inspectedEntries++;
        if (inspectedEntries > 4096) return failure(true);
        const entry = read(registry, String(index));
        if (!Array.isArray(entry)) continue;
        const ids = read(entry, '0');
        const modules = read(entry, '1');
        if (!Array.isArray(ids) || modules === null ||
            (typeof modules !== 'object' && typeof modules !== 'function') ||
            Array.isArray(modules) || !(read(ids, 'length') >= 1))
          continue;
        const firstId = read(ids, '0');
        if (typeof firstId === 'string' || typeof firstId === 'number') {
          processed = true;
          break;
        }
      }
      if (processed) {
        result.processedEntryShapeCandidates++;
      result.passedAllPredicates++;
      } else result.noProcessedEntryShape++;
    }
    return result;
  } catch {
    return failure(false);
  }
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
        browserModule.diagnostics.inspectRspackExportsScript(moduleId, keys), true
      );
    }
    case 'rspackRegistryShape': {
      if (browserModule?.name !== 'ChatGPT')
        throw new Error('Rspack registry shape is limited to ChatGPT diagnostics.');
      if (args?.inspect !== true)
        throw new Error('Rspack registry shape requires inspect=true.');
      return window.webContents.executeJavaScript(pageScript(rspackRegistryShapePage), true);
    }
    case 'rspackTiming': {
      if (browserModule?.name !== 'ChatGPT' || args?.clear !== true)
        throw new Error('Rspack timing clearing requires ChatGPT and clear=true.');
      return window.webContents.executeJavaScript(pageScript(rspackTimingPage, true), true);
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
  rspackTimingPage,
  rspackRegistryShapePage
};
