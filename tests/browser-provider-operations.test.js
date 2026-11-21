const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { randomUUID } = require("node:crypto");
const { EventEmitter } = require("node:events");

const root = path.resolve(__dirname, "..");

function loadChatGptModule() {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chatgpt-test-"));
  fs.cpSync(path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "modules"), directory, { recursive: true });
  return require(path.join(directory, "entry.js"));
}

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json" }
  });
}

function textResponse(value, status = 200, contentType = "text/plain") {
  return new Response(value, {
    status,
    headers: { "content-type": contentType }
  });
}

function mockSession(fetch, deviceId = null) {
  const cookies = new Map();
  return {
    fetch,
    cookies: {
      get: async ({ name } = {}) => {
        if (cookies.has(name))
          return [{ name, value: cookies.get(name) }];
        return deviceId ? [{ value: deviceId }] : [];
      },
      set: async cookie => {
        cookies.set(cookie.name, cookie.value);
      }
    },
    __cookies: cookies
  };
}

function completedConversation(
  conversationId,
  projectId = null,
  model = "served-test",
  requestMessageId = null,
  resolvedModel = null
) {
  const conversation = {
    conversation_id: conversationId,
    gizmo_id: projectId,
    current_node: "assistant-1",
    mapping: {
      "assistant-1": {
        parent: requestMessageId ? "request-1" : null,
        message: {
          id: "assistant-1",
          author: { role: "assistant" },
          status: "finished_successfully",
          content: { parts: ["answer"] },
          metadata: { model_slug: model }
        }
      }
    }
  };
  if (requestMessageId) {
    conversation.mapping["request-1"] = {
      parent: null,
      message: {
        id: requestMessageId,
        author: { role: "user" },
        status: "finished_successfully",
        content: { parts: ["question"] },
        metadata: { resolved_model_slug: resolvedModel }
      }
    };
  }
  return conversation;
}

test("browser transport polls long provider operations through short HTTP requests", () => {
  const electron = fs.readFileSync(path.join(root, "electron", "main.js"), "utf8");
  const transport = fs.readFileSync(
    path.join(root, "transports", "Mezhs.Browser.Electron", "ElectronBrowserTransport.cs"),
    "utf8"
  );
  const contract = fs.readFileSync(
    path.join(root, "transports", "Mezhs.Browser.Abstractions", "IChatBrowserTransport.cs"),
    "utf8"
  );

  assert.match(electron, /requestUrl\.pathname === "\/invoke"/);
  assert.match(electron, /requestUrl\.pathname\.startsWith\("\/invoke\/"\)/);
  assert.match(electron, /writeJson\(response, 202, \{ operationId \}\)/);
  assert.match(electron, /queueProviderOperation\(body\)/);
  assert.doesNotMatch(electron, /request\.url === "\/prompt"/);
  assert.doesNotMatch(electron, /request\.url === "\/fetch"/);

  assert.match(transport, /Timeout = TimeSpan\.FromSeconds\(10\)/);
  assert.match(transport, /OperationPollInterval = TimeSpan\.FromSeconds\(2\)/);
  assert.match(transport, /GetAsync\(/);
  assert.doesNotMatch(transport, /FromMinutes\(6\)/);

  assert.match(contract, /InvokeAsync<TResult>/);
  assert.match(contract, /InvokeWithProgressAsync<TResult>/);
  assert.match(contract, /BrowserOperationProgress/);
  assert.match(electron, /reportProgress/);
  assert.match(transport, /reportProgress\?\.Invoke\(progress\)/);
  assert.match(electron, /state\.code = typeof error\?\.code === "string"/);
  assert.match(transport, /state\.Code,[\s\S]*"authorization_required"[\s\S]*BrowserAuthorizationRequiredException/);
  assert.doesNotMatch(contract, /SendPromptAsync|SendWebRequestAsync|BrowserWebRequest|BrowserWebResponse/);
});

test("ChatGPT authorization rejects a stale token that the backend returns 401 for", async () => {
  const chatgpt = loadChatGptModule();
  let backendStatus = 401;
  const session = mockSession(async url => {
    const target = new URL(String(url));
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "stale-token" });
    if (target.pathname === "/backend-api/models")
      return jsonResponse({}, backendStatus);
    throw new Error(`Unexpected request ${target}`);
  });
  const window = { webContents: { session } };

  assert.equal(await chatgpt.isAuthorized(window), false);
  backendStatus = 200;
  assert.equal(await chatgpt.isAuthorized(window), true);
});

test("provider response lifetime is not capped by wall-clock deadlines", () => {
  const files = [
    ...fs.readdirSync(path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "modules"))
      .filter(name => name.endsWith(".js"))
      .map(name => path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "modules", name)),
    path.join(root, "integrations", "Mezhs.Integrations.Grok", "browser", "grok.ts"),
    path.join(root, "integrations", "Mezhs.Integrations.Gemini", "browser", "gemini.ts")
  ];

  for (const file of files) {
    const source = fs.readFileSync(file, "utf8");
    assert.doesNotMatch(source, /response timed out/i, file);
    assert.doesNotMatch(source, /CONVERSATION_POLL_ATTEMPTS/, file);
    assert.doesNotMatch(source, /responseDeadline\s*=|const deadline = Date\.now\(\) \+ 180000/, file);
  }
});

test("ChatGPT project discovery is no longer a separate JavaScript operation", () => {
  const chatgpt = loadChatGptModule();
  assert.equal(chatgpt.operations.getProjects, undefined);
  const accountSource = fs.readFileSync(path.join(root,
    "integrations", "Mezhs.Integrations.ChatGpt", "ChatGptIntegration.cs"), "utf8");
  assert.match(accountSource, /ChatGptTypedHttpEndpoints\.Projects/);
});


function noUiWindow() {
  return {
    webContents: {
      executeJavaScript: async source => {
        assert.match(source, /lXE/);
        assert.doesNotThrow(() => new Function(`return ${source};`));
        assert.doesNotMatch(source, /document\.querySelector|querySelectorAll|sendInputEvent/);
        return {
          headers: {
            "OpenAI-Sentinel-Chat-Requirements-Token": "native-requirements",
            "OpenAI-Sentinel-Proof-Token": "native-proof",
            "OpenAI-Sentinel-Turnstile-Token": "native-turnstile"
          },
          forceLogin: false
        };
      }
    }
  };
}

function directSession({ conversationId = "conv-test", projectId = null, conversationStatus = 200 } = {}) {
  const state = { postCount: 0, requests: [], posted: null };
  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));
    state.requests.push({ path: target.pathname, method: options.method || "GET" });
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token", user: { id: "account-test" } });
    if (target.pathname === "/backend-api/settings/user_last_used_model_config")
      return textResponse("");
    if (target.pathname === "/backend-api/conversation/init")
      return jsonResponse({ default_model_slug: "gpt-5-6-thinking" });
    if (target.pathname === "/backend-api/f/conversation/prepare") {
      assert.equal(options.method, "POST");
      return jsonResponse({ conduit_token: "prepared-by-server" });
    }
    if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
      state.postCount++;
      state.posted = JSON.parse(options.body);
      assert.equal(options.headers["x-conduit-token"], "prepared-by-server");
      assert.equal(options.headers["OpenAI-Sentinel-Chat-Requirements-Token"], "native-requirements");
      assert.equal(options.headers["OpenAI-Sentinel-Proof-Token"], "native-proof");
      assert.equal(options.headers["OpenAI-Sentinel-Turnstile-Token"], "native-turnstile");
      if (conversationStatus !== 200)
        return textResponse("Direct submission not allowed", conversationStatus);
      return textResponse(
        `data: {"conversation_id":"${conversationId}"}\n\ndata: [DONE]\n\n`,
        200,
        "text/event-stream"
      );
    }
    if (target.pathname === `/backend-api/conversation/${conversationId}`) {
      if (!state.posted)
        return jsonResponse({ conversation_id: conversationId });
      return jsonResponse(completedConversation(
        conversationId,
        projectId,
        "gpt-5-6-thinking",
        state.posted.messages[0].id
      ));
    }
    throw new Error(`Unexpected semantic request ${target}`);
  });
  return { session, state };
}

test("ChatGPT account sends project turns through semantic HTTP with the requested effort", async () => {
  const chatgpt = loadChatGptModule();
  const { session, state } = directSession({ projectId: "g-p-test" });
  const result = await chatgpt.operations.newChat({
    window: noUiWindow(), session,
    args: { messageId: randomUUID(),
      prompt: "test project",
      model: "gpt-5-6-thinking::thinking-effort=extended",
      projectId: "g-p-test",
      files: []
    },
    sleep: async () => {}
  });
  assert.equal(state.postCount, 1);
  assert.equal(state.posted.model, "gpt-5-6-thinking");
  assert.equal(state.posted.thinking_effort, "extended");
  assert.equal(state.posted.conversation_mode.gizmo_id, "g-p-test");
  assert.equal(state.posted.messages[0].content.parts[0], "test project");
  assert.equal(result.conversationId, "conv-test");
  assert.equal(result.text, "answer");
});

test("ChatGPT account continuations target the known conversation without UI navigation", async () => {
  const chatgpt = loadChatGptModule();
  const { session, state } = directSession();
  const result = await chatgpt.operations.send({
    window: noUiWindow(), session,
    args: { messageId: randomUUID(),
      conversationId: "conv-test",
      parentMessageId: "assistant-previous",
      prompt: "continue",
      files: []
    },
    sleep: async () => {}
  });
  assert.equal(state.postCount, 1);
  assert.equal(state.posted.conversation_id, "conv-test");
  assert.equal(state.posted.parent_message_id, "assistant-previous");
  assert.equal(state.requests.filter(x => x.path === "/backend-api/conversation/conv-test").length, 2);
  assert.equal(result.text, "answer");
});

test("ChatGPT does not replay a turn when the backend rejects direct submission", async () => {
  const chatgpt = loadChatGptModule();
  const { session, state } = directSession({ conversationStatus: 403 });
  await assert.rejects(
    chatgpt.operations.send({
      window: noUiWindow(), session,
      args: { messageId: randomUUID(), conversationId: "conv-test", parentMessageId: "old", prompt: "once", files: [] },
      sleep: async () => {}
    }),
    /HTTP 403/
  );
  assert.equal(state.postCount, 1);
  assert.equal(state.requests.filter(x => x.path === "/backend-api/conversation/conv-test").length, 1);
});

test("ChatGPT authenticated modules avoid DOM and native input submission", () => {
  const directory = path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "modules");
  for (const name of ["account-messages.js", "account-files.js", "account-models.js", "account-results.js", "account-stream.js", "account-transport.js"]) {
    const source = fs.readFileSync(path.join(directory, name), "utf8");
    assert.doesNotMatch(source, /querySelector|document[.]|contenteditable|focusNativeComposer|insertText[(]|sendInputEvent[(]/);
    assert.doesNotMatch(source, /Network[.]requestWillBeSent|Network[.]streamResourceContent/);
  }
  const messages = require(path.join(directory, "account-messages.js"));
  assert.equal(typeof messages.sendAccountMessage, "function");
  assert.equal(typeof messages.postConversationTurn, "function");
  assert.match(messages.postConversationTurn.toString(), /nativeChatRequirementsHeaders/);
  assert.match(messages.sendAccountMessage.toString(), /sendApiAccountMessage/);
});
