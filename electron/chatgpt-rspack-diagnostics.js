"use strict";

// Private ChatGPT frontend internals: diagnostic observation only. This
// function is serialized and executed in the authenticated renderer main world.
// Do not invoke inspected exports or reuse these historical IDs for discovery.
async function inspectRspackExportsPage(moduleId, exportKeys) {
  const resources = performance.getEntriesByType("resource")
    .map(item => String(item.name || ""))
    .filter(url => url.startsWith("https://chatgpt.com/cdn/assets/633146.") &&
      url.split("?")[0].endsWith(".js"));
  if (!resources.length)
    throw new Error("ChatGPT Rspack runtime was not observed in Resource Timing.");
  const runtime = await import(resources[resources.length - 1]);
  const loader = runtime?.__webpack_require__;
  const record = loader?.c?.[moduleId];
  if (!record)
    throw new Error("Requested Rspack module was not present in the loaded cache.");
  const exports = record.exports;
  const markers = ["chatReq", "turnstileToken", "proofToken"];
  const context = (source, marker) => {
    const index = source.indexOf(marker);
    return index < 0 ? null
      : source.slice(Math.max(0, index - 90), Math.min(source.length, index + marker.length + 130));
  };
  const results = exportKeys.map(key => {
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

module.exports = { inspectRspackExportsPage };
