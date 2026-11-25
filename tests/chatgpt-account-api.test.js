const test = require("node:test");
const assert = require("node:assert/strict");
const { executeBrowserHttp } = require("../electron/browser-http");
const chatgpt = require("../integrations/Mezhs.Integrations.ChatGpt/browser/modules/entry.js");

function session(fetch) {
  return { fetch, cookies: { get: async () => [], set: async () => {} } };
}
function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status, headers: { "content-type": "application/json" }
  });
}
const nativeWindow = { webContents: { executeJavaScript: async () => ({
  forceLogin: false, headers: {
    "openai-sentinel-chat-requirements-token": "native-requirements"
  }
}) } };
const turnArgs = {
  method: "POST", path: "/backend-api/f/conversation", accept: "text/event-stream",
  stream: true,
  body: JSON.stringify({ action: "next", messages: [{ id: "id" }] }),
  nativeHook: "chatgptTurn",
  nativeArgs: {
    clientSessionId: "b534dc12-6ee8-4f33-886b-dc112c04801a",
    prepare: { action: "next", partial_query: { content: { parts: ["message"] } } }
  }
};

test("one native-prepared ChatGPT POST emits ordered stream events while active", async () => {
  let count = 0;
  const events = [];
  let posted;
  const actor = session(async (url, options = {}) => {
    const endpoint = new URL(String(url)).pathname;
    if (endpoint === "/api/auth/session")
      return json({ accessToken: "private-token" });
    if (endpoint === "/backend-api/f/conversation/prepare")
      return json({ conduit_token: "prepared-token" });
    if (endpoint === "/backend-api/f/conversation") {
      count++;
      posted = { headers: options.headers, body: options.body, signal: options.signal };
      const bytes = new TextEncoder().encode(
        'data: {"conversation_id":"conv-native"}\n\n' +
        'data: {"type":"message_marker","marker":"cot_token"}\n\n' +
        'data: [DONE]\n\n');
      let offset = 0;
      const body = new ReadableStream({
        pull(controller) {
          if (offset >= bytes.length) return controller.close();
          controller.enqueue(bytes.slice(offset, offset + 7));
          offset += 7;
        }
      });
      return new Response(body, { status: 200,
        headers: { "content-type": "text/event-stream" } });
    }
    throw new Error("Unexpected network destination.");
  });
  const result = await executeBrowserHttp({
    window: nativeWindow, session: actor, args: turnArgs,
    reportEvent: event => events.push(JSON.parse(event))
  }, chatgpt.http);
  assert.equal(result.statusCode, 200);
  assert.equal(result.body, "");
  assert.equal(count, 1, "an uncertain POST must never be replayed");
  assert.equal(posted.headers.Authorization, "Bearer private-token");
  assert.equal(posted.headers["x-conduit-token"], "prepared-token");
  assert.equal(posted.headers["openai-sentinel-chat-requirements-token"],
    "native-requirements");
  assert.equal(posted.body, turnArgs.body);
  assert.deepEqual(events.map(value => value.conversation_id || value.marker),
    ["conv-native", "cot_token"]);
});

test("native preparation failure is classified before conversation submission", async () => {
  let submitted = 0;
  const actor = session(async url => {
    const endpoint = new URL(String(url)).pathname;
    if (endpoint === "/api/auth/session") return json({ accessToken: "token" });
    if (endpoint === "/backend-api/f/conversation/prepare")
      return json({ conduit_token: "conduit" });
    if (endpoint === "/backend-api/f/conversation") submitted++;
    throw Error("Unexpected endpoint");
  });
  const failedWindow = { webContents: {
    executeJavaScript: async () => { throw Error("private native challenge detail"); }
  } };
  const progress = [];
  await assert.rejects(executeBrowserHttp({
    window: failedWindow, session: actor, args: turnArgs, reportEvent: () => {},
    reportProgress: value => progress.push(value)
  }, chatgpt.http), error => {
    assert.equal(error.code, "request_not_submitted");
    assert.match(error.message, /before request submission/);
    assert.doesNotMatch(error.message, /private native challenge detail/);
    return true;
  });
  assert.equal(submitted, 0, "failed preparation must not submit the conversation");
  assert.ok(progress.some(value => value.detail === "Preparing ChatGPT conduit token."));
  assert.ok(progress.some(value => value.detail === "Preparing ChatGPT native requirements."));
  assert.ok(progress.some(value =>
    value.detail === "ChatGPT native requirements failed (other)."));
  assert.ok(progress.every(value => !JSON.stringify(value).includes("private native challenge detail")),
    "native failure progress must not include arbitrary error text or challenge data");
});

test("native requirements failure categories stay fixed and no conversation is submitted", async () => {
  const scenarios = [
    ["ChatGPT native requirements provider was not observed in this document (resourceEntries=3). [page=root,ready=complete,scripts=unauth_mweb,resources=unauth_mweb]",
      "runtime_not_observed"],
    ["ChatGPT native requirements provider export is unavailable (runtimeCandidates=17).",
      "provider_export_unavailable"],
    ["ChatGPT requirements prepare failed with HTTP 403", "requirements_prepare_http_failure"],
    ["ChatGPT native chat requirements require login.", "login_required"]
  ];
  let submitted = 0;
  const actor = session(async url => {
    const endpoint = new URL(String(url)).pathname;
    if (endpoint === "/api/auth/session") return json({ accessToken: "secret-not-for-progress" });
    if (endpoint === "/backend-api/f/conversation/prepare")
      return json({ conduit_token: "private-conduit" });
    if (endpoint === "/backend-api/f/conversation") submitted++;
    throw Error("Unexpected endpoint");
  });
  for (const [message, category] of scenarios) {
    const progress = [];
    const window = { webContents: {
      executeJavaScript: async () => { throw Error(message); }
    } };
    await assert.rejects(executeBrowserHttp({
      window, session: actor, args: turnArgs, reportEvent: () => {},
      reportProgress: value => progress.push(value)
    }, chatgpt.http), error => {
      assert.equal(error.code, "request_not_submitted");
      assert.doesNotMatch(error.message, /runtimeCandidates|resourceEntries|secret-not-for-progress/);
      return true;
    });
    const expected = category === 'runtime_not_observed'
      ? 'ChatGPT native requirements failed (runtime_not_observed) ' +
        '[page=root,ready=complete,scripts=unauth_mweb,resources=unauth_mweb].'
      : 'ChatGPT native requirements failed (' + category + ').';
    assert.equal(progress.at(-1)?.detail, expected,
      'latest-only Electron polling must retain the safe renderer classification');
    assert.ok(progress.every(value => !JSON.stringify(value).includes(message)),
      'progress must not include private browser exception text');
  }
  assert.equal(submitted, 0);
});

test("native preparation cancellation is not converted into a normal failure", async () => {
  for (const mode of ["signal", "abort-error"]) {
    let submitted = 0;
    const controller = new AbortController();
    const actor = session(async url => {
      const endpoint = new URL(String(url)).pathname;
      if (endpoint === "/api/auth/session") return json({ accessToken: "token" });
      if (endpoint === "/backend-api/f/conversation/prepare")
        return json({ conduit_token: "conduit" });
      if (endpoint === "/backend-api/f/conversation") submitted++;
      throw Error("Unexpected endpoint");
    });
    const cancelledWindow = { webContents: {
      executeJavaScript: async () => {
        if (mode === "signal") controller.abort();
        throw new DOMException("Cancelled native preparation", "AbortError");
      }
    } };
    await assert.rejects(executeBrowserHttp({
      window: cancelledWindow, session: actor, args: turnArgs, signal: controller.signal,
      reportEvent: () => {}
    }, chatgpt.http), error => {
      assert.equal(error.name, "AbortError");
      assert.notEqual(error.code, "request_not_submitted");
      return true;
    });
    assert.equal(submitted, 0);
  }
});

test("ambiguous native POST failure is surfaced without replay", async () => {
  let posted = 0;
  const actor = session(async (url) => {
    const endpoint = new URL(String(url)).pathname;
    if (endpoint === "/api/auth/session") return json({ accessToken: "token" });
    if (endpoint === "/backend-api/f/conversation/prepare")
      return json({ conduit_token: "conduit" });
    if (endpoint === "/backend-api/f/conversation") {
      posted++;
      throw Error("connection interrupted after submission");
    }
    throw Error("unexpected endpoint");
  });
  await assert.rejects(executeBrowserHttp({
    window: nativeWindow, session: actor, args: turnArgs,
    reportEvent: () => {}
  }, chatgpt.http), /connection interrupted/);
  assert.equal(posted, 1);
});

test("fetch-stage errors cannot masquerade as unsubmitted requests", async () => {
  let submitted = 0;
  const actor = session(async url => {
    const endpoint = new URL(String(url)).pathname;
    if (endpoint === "/api/auth/session") return json({ accessToken: "token" });
    if (endpoint === "/backend-api/f/conversation/prepare")
      return json({ conduit_token: "conduit" });
    if (endpoint === "/backend-api/f/conversation") {
      submitted++;
      const error = new Error("private provider fetch error");
      error.code = "request_not_submitted";
      throw error;
    }
    throw new Error("Unexpected endpoint");
  });
  await assert.rejects(executeBrowserHttp({
    window: nativeWindow, session: actor, args: turnArgs, reportEvent: () => {}
  }, chatgpt.http), error => {
    assert.notEqual(error.code, "request_not_submitted");
    assert.match(error.message, /delivery may be uncertain/);
    assert.doesNotMatch(error.message, /private provider fetch error/);
    return true;
  });
  assert.equal(submitted, 1, "uncertain conversation fetch must not be replayed");
});

test("provider streaming HTTP errors expose only bounded safe status fields", async () => {
  const actor = session(async (url) => {
    const endpoint = new URL(String(url)).pathname;
    if (endpoint === "/api/auth/session") return json({ accessToken: "token" });
    if (endpoint === "/backend-api/f/conversation/prepare")
      return json({ conduit_token: "conduit" });
    if (endpoint === "/backend-api/f/conversation")
      return json({ detail: { code: "conversation_inaccessible",
        message: "private-provider-detail" } }, 404);
    throw Error("unexpected endpoint");
  });
  const response = await executeBrowserHttp({
    window: nativeWindow, session: actor, args: turnArgs,
    reportEvent: () => {}
  }, chatgpt.http);
  assert.equal(response.statusCode, 404);
  assert.equal(response.providerCode, "conversation_inaccessible");
  assert.equal(response.body, "");
  assert.doesNotMatch(JSON.stringify(response), /private-provider-detail|conduit/);
});

test("native auth and prepare JSON reject oversized success bodies before submission", async () => {
  for (const oversizedAt of ["/api/auth/session", "/backend-api/f/conversation/prepare"]) {
    let cancelled = false;
    let submissions = 0;
    const actor = session(async url => {
      const endpoint = new URL(String(url)).pathname;
      if (endpoint === oversizedAt) {
        return new Response(new ReadableStream({
          start(controller) {
            controller.enqueue(new Uint8Array(1024 * 1024 + 1));
          },
          cancel() { cancelled = true; }
        }), { status: 200, headers: { "content-type": "application/json" } });
      }
      if (endpoint === "/api/auth/session") return json({ accessToken: "private-token" });
      if (endpoint === "/backend-api/f/conversation/prepare")
        return json({ conduit_token: "conduit" });
      if (endpoint === "/backend-api/f/conversation") {
        submissions++;
        return new Response(null, { status: 200 });
      }
      throw new Error("Unexpected endpoint.");
    });
    await assert.rejects(executeBrowserHttp({
      window: nativeWindow, session: actor, args: turnArgs,
      reportEvent: () => {}
    }, chatgpt.http), error => {
      assert.equal(error.code, 'request_not_submitted');
      assert.match(error.message, /before request submission/);
      return true;
    });
    assert.equal(cancelled, true, "oversized native response must cancel its source");
    assert.equal(submissions, 0, "must not send a turn after a failed prepare or auth read");
  }
});
