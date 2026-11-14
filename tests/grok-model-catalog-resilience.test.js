// Grok catalog recovery contract. Keep retry semantics provider-owned; no turn replay.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const grokFile = path.join(
  __dirname, "..", "integrations", "Mezhs.Integrations.Grok", "browser", "grok.ts"
);

function loadGrok() {
  const local = { exports: {} };
  new Function("module", "require", fs.readFileSync(grokFile, "utf8"))(local, require);
  return local.exports;
}

function catalog() {
  return new Response(JSON.stringify({
    modes: [{ id: "auto", title: "Auto", availability: { available: {} } }],
    defaultModeId: "auto"
  }), { status: 200, headers: { "content-type": "application/json" } });
}

function brokenBody() {
  return new Response(new ReadableStream({
    start(controller) {
      controller.error(new Error("net::ERR_HTTP2_PROTOCOL_ERROR"));
    }
  }), { status: 200 });
}

async function withCatalogFetch(fetch, action) {
  const originalFetch = global.fetch;
  const originalWarn = console.warn;
  const warnings = [];
  global.fetch = fetch;
  console.warn = (...args) => warnings.push(args.join(" "));
  try {
    return await action(warnings);
  } finally {
    global.fetch = originalFetch;
    console.warn = originalWarn;
  }
}

test("Grok catalog retries a known transient fetch error with identical read-only POST", async () => {
  const grok = loadGrok();
  const calls = [];
  await withCatalogFetch(async (url, options) => {
    calls.push({ url, options });
    if (calls.length === 1) {
      const error = new Error("private-cookie=should-not-log");
      error.code = "net::ERR_NETWORK_IO_SUSPENDED";
      throw error;
    }
    return catalog();
  }, async warnings => {
    assert.deepEqual(await grok.pageOperations.models(), [{ id: "auto", name: "Auto" }]);
    assert.equal(calls.length, 2);
    for (const call of calls) {
      assert.equal(call.url, "https://grok.com/rest/modes");
      assert.equal(call.options.method, "POST");
      assert.equal(call.options.credentials, "include");
      assert.equal(call.options.cache, "no-store");
    }
    assert.equal(calls[0].options.body, calls[1].options.body);
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /Grok transient POST \/rest\/modes ERR_NETWORK_IO_SUSPENDED; retry 1\/3/);
    assert.doesNotMatch(warnings.join(" "), /private-cookie|locale|token|secret/);
  });
});

test("Grok retries transient response-body failure, not just failed fetch", async () => {
  const grok = loadGrok();
  let reads = 0;
  await withCatalogFetch(async () => ++reads === 1 ? brokenBody() : catalog(), async warnings => {
    assert.deepEqual(await grok.pageOperations.models(), [{ id: "auto", name: "Auto" }]);
    assert.equal(reads, 2);
    assert.match(warnings[0], /ERR_HTTP2_PROTOCOL_ERROR/);
  });
});

test("Grok retries are bounded and preserve last transient exception", async () => {
  const grok = loadGrok();
  let reads = 0;
  const original = new Error("net::ERR_CONNECTION_RESET");
  await withCatalogFetch(async () => {
    reads++;
    throw original;
  }, async warnings => {
    await assert.rejects(grok.pageOperations.models(), error => error === original);
    assert.equal(reads, 4);
    assert.equal(warnings.length, 3);
    assert.ok(warnings.every(w => !w.includes("undefined")));
  });
});

test("Grok never retries HTTP auth/rate-limit/server status or successful malformed data", async () => {
  const grok = loadGrok();
  for (const status of [401, 403, 404, 429, 500]) {
    let reads = 0;
    await withCatalogFetch(async () => {
      reads++;
      return new Response("not authorized", { status });
    }, async warnings => {
      await assert.rejects(grok.pageOperations.models(), new RegExp(`HTTP ${status}`));
      assert.equal(reads, 1, `HTTP ${status} must not be retried`);
      assert.equal(warnings.length, 0);
    });
  }
  let malformedReads = 0;
  await withCatalogFetch(async () => {
    malformedReads++;
    return new Response("invalid json", { status: 200 });
  }, async warnings => {
    await assert.rejects(grok.pageOperations.models(), SyntaxError);
    assert.equal(malformedReads, 1);
    assert.equal(warnings.length, 0);
  });
  // A parser error may quote text that resembles a Chromium error code.
  // That must not turn malformed provider JSON into a retried network request.
  let codeInJsonReads = 0;
  await withCatalogFetch(async () => {
    codeInJsonReads++;
    return new Response("ERR_HTTP2_PROTOCOL_ERROR", { status: 200 });
  }, async warnings => {
    await assert.rejects(grok.pageOperations.models(), SyntaxError);
    assert.equal(codeInJsonReads, 1);
    assert.equal(warnings.length, 0);
  });
  let invalidCatalogReads = 0;
  await withCatalogFetch(async () => {
    invalidCatalogReads++;
    return new Response(JSON.stringify({ modes: [] }), { status: 200 });
  }, async warnings => {
    await assert.rejects(grok.pageOperations.models(), /no recognizable models/);
    assert.equal(invalidCatalogReads, 1);
    assert.equal(warnings.length, 0);
  });
});

test("Grok does not retry an unclassified transport error", async () => {
  const grok = loadGrok();
  let reads = 0;
  await withCatalogFetch(async () => {
    reads++;
    throw new TypeError("Failed to fetch");
  }, async warnings => {
    await assert.rejects(grok.pageOperations.models(), /Failed to fetch/);
    assert.equal(reads, 1);
    assert.equal(warnings.length, 0);
  });
});

test("Grok catalog read retry precedes one native send; failure never starts a prompt", async () => {
  const grok = loadGrok();
  let reads = 0;
  let sends = 0;
  const context = {
    window: { async loadURL(url) { assert.equal(url, "https://grok.com/"); } },
    args: { prompt: "secret user prompt", model: "auto" },
    page: {
      async invoke(operation, args) {
        if (operation === "selectModel")
          return grok.pageOperations.selectModel({ args, sleep: async () => {} });
        if (operation === "sendPrompt") {
          sends++;
          return { text: "done", chatUrl: "https://grok.com/c/test" };
        }
        throw new Error(`unexpected ${operation}`);
      }
    }
  };
  await withCatalogFetch(async () => {
    reads++;
    if (reads === 1) throw new Error("net::ERR_HTTP2_PROTOCOL_ERROR");
    return catalog();
  }, async warnings => {
    const result = await grok.operations.newChat(context);
    assert.equal(result.text, "done");
    assert.equal(reads, 2);
    assert.equal(sends, 1);
    assert.ok(warnings.every(line => !line.includes("secret user prompt")));
  });
  sends = 0;
  await withCatalogFetch(async () => new Response("unauthorized", { status: 401 }), async () => {
    await assert.rejects(grok.operations.newChat(context), /HTTP 401/);
    assert.equal(sends, 0);
  });
});
