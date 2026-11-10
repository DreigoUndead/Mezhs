// Shared renderer-side Rspack discovery for Account and loopback diagnostics.
function discoverLiveRspack() {
  const cacheKey = Symbol.for("mezhs.chatgpt.rspackLoaders.v1");
  const isObject = value => value !== null &&
    (typeof value === "object" || typeof value === "function");
  const own = (object, key) => {
    if (!isObject(object)) return undefined;
    const descriptor = Object.getOwnPropertyDescriptor(object, key);
    return descriptor && Object.prototype.hasOwnProperty.call(descriptor, "value")
      ? descriptor.value : undefined;
  };
  const names = (object, limit, subject) => {
    const entries = Object.getOwnPropertyNames(object);
    if (entries.length > limit)
      throw new Error("ChatGPT Rspack " + subject + " scan limit exceeded.");
    return entries;
  };
  const validLoader = loader => typeof loader === "function" &&
    typeof own(loader, "c") === "object" && own(loader, "c") !== null;
  let loaders;
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, cacheKey);
  if (descriptor) {
    const record = descriptor.value;
    loaders = own(record, "loaders");
    if (descriptor.configurable || descriptor.writable || descriptor.enumerable ||
        own(record, "version") !== 1 || !Object.isFrozen(record) ||
        !Array.isArray(loaders) || !Object.isFrozen(loaders) ||
        loaders.length < 1 || loaders.length > 8 || !loaders.every(validLoader))
      throw new Error("ChatGPT Rspack shared loader cache is invalid.");
  } else {
    const registries = [];
    for (const name of names(globalThis, 5000, "global")) {
      if (!/^webpackChunk[A-Za-z0-9_$]{1,80}$/.test(name)) continue;
      const registry = own(globalThis, name);
      if (!Array.isArray(registry)) continue;
      const push = own(registry, "push");
      if (typeof push !== "function" || push === Array.prototype.push ||
          /\[native code\]/.test(Function.prototype.toString.call(push)))
        continue;
      const length = own(registry, "length");
      if (!Number.isSafeInteger(length) || length < 1 || length > 2048)
        continue;
      let processedEntry = false;
      for (let index = 0; index < length; index++) {
        const entry = own(registry, String(index));
        const ids = own(entry, "0");
        const modules = own(entry, "1");
        if (!Array.isArray(entry) || !Array.isArray(ids) ||
            !isObject(modules) || Array.isArray(modules) ||
            own(ids, "length") < 1)
          continue;
        const firstId = own(ids, "0");
        if (typeof firstId === "string" || typeof firstId === "number") {
          processedEntry = true;
          break;
        }
      }
      if (!processedEntry) continue;
      registries.push({ registry, push });
      if (registries.length > 8)
        throw new Error("ChatGPT Rspack registry scan limit exceeded.");
    }
    if (!registries.length)
      throw new Error("ChatGPT live Rspack chunk registry was not observed.");
    loaders = [];
    for (const { registry, push } of registries) {
      const chunkId = "mezhs_probe_" + crypto.randomUUID();
      let captured;
      Reflect.apply(push, registry, [[[chunkId], Object.create(null),
        loader => { captured = loader; }]]);
      if (!validLoader(captured))
        throw new Error("ChatGPT Rspack registry did not supply a live loader.");
      if (!loaders.includes(captured)) loaders.push(captured);
    }
    if (!loaders.length) throw new Error("ChatGPT Rspack loader capture failed.");
    loaders = Object.freeze(loaders);
    Object.defineProperty(globalThis, cacheKey, {
      value: Object.freeze({ version: 1, loaders }),
      writable: false, configurable: false, enumerable: false
    });
  }
  function forEachModule(visit) {
    let inspected = 0;
    for (const loader of loaders) {
      const cache = own(loader, "c");
      if (typeof cache !== "object" || cache === null)
        throw new Error("ChatGPT Rspack loader cache is no longer valid.");
      const ids = names(cache, 20000, "module");
      inspected += ids.length;
      if (inspected > 24000)
        throw new Error("ChatGPT Rspack combined module scan limit exceeded.");
      for (const id of ids) {
        const exports = own(own(cache, id), "exports");
        if (isObject(exports)) visit(id, exports);
      }
    }
  }
  return {
    findCoordinator() {
      let coordinator;
      const markers = ["chatRequirements", "proofToken",
        "requirementsKey", "turnstileToken"];
      forEachModule((_id, exports) => {
        const candidate = own(exports, "f");
        if (typeof candidate !== "function" || own(candidate, "length") !== 1)
          return;
        const source = Function.prototype.toString.call(candidate);
        if (!markers.every(marker => source.includes(marker))) return;
        if (coordinator && coordinator !== candidate)
          throw new Error("ChatGPT native requirements coordinator is ambiguous.");
        coordinator = candidate;
      });
      if (!coordinator)
        throw new Error("ChatGPT native requirements coordinator is unavailable.");
      return coordinator;
    },
    moduleExports(moduleId) {
      let match;
      forEachModule((id, exports) => {
        if (id !== moduleId) return;
        if (match && match !== exports)
          throw new Error("Requested Rspack module is ambiguous.");
        match = exports;
      });
      if (!match)
        throw new Error("Requested Rspack module was not present in the live cache.");
      return match;
    },
    own, names
  };
}

function discoverySource() {
  return "(" + discoverLiveRspack.toString() + ")()";
}

function inspectPage(runtime, moduleId, exportKeys) {
  const exports = runtime.moduleExports(moduleId);
  const markers = ["chatReq", "turnstileToken", "proofToken"];
  const context = (source, marker) => {
    const index = source.indexOf(marker);
    return index < 0 ? null
      : source.slice(Math.max(0, index - 90),
        Math.min(source.length, index + marker.length + 130));
  };
  const results = exportKeys.map(key => {
    const value = runtime.own(exports, key);
    if (typeof value !== "function")
      return { exportKey: key, type: typeof value };
    const source = Function.prototype.toString.call(value);
    return {
      exportKey: key,
      name: String(runtime.own(value, "name") || "").slice(0, 64),
      arity: runtime.own(value, "length"),
      sourceLength: source.length,
      start: source.slice(0, 160),
      end: source.slice(-100),
      markerContexts: Object.fromEntries(markers.map(marker => [marker, context(source, marker)]))
    };
  });
  return {
    moduleId,
    availableExportKeys: runtime.names(exports, 256, "export").slice(0, 30),
    results
  };
}

function inspectRspackExportsScript(moduleId, exportKeys) {
  return "(() => { const runtime = " + discoverySource() +
    "; return (" + inspectPage.toString() + ")(runtime, " +
    JSON.stringify(moduleId) + ", " + JSON.stringify(exportKeys) + "); })()";
}

module.exports = { discoverySource, inspectRspackExportsScript };
