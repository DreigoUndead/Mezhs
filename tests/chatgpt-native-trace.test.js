const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const path = require("node:path");

const traceModule = require(path.join(
  __dirname,
  "diagnostics",
  "chatgpt-native-trace-module.js"
));

class FakeDebugger extends EventEmitter {
  constructor() {
    super();
    this.attached = false;
    this.commands = [];
    this.detached = false;
  }

  isAttached() {
    return this.attached;
  }

  attach(version) {
    assert.equal(version, "1.3");
    this.attached = true;
  }

  detach() {
    this.detached = true;
    this.attached = false;
  }

  async sendCommand(method, args = {}) {
    this.commands.push({ method, args });
    if (method === "Debugger.getScriptSource") {
      assert.equal(args.scriptId, "42");
      return {
        scriptSource:
          "function nativeSubmit(){const marker='before';return dispatchConversation();}function after(){}"
      };
    }
    return {};
  }
}

function fakeWindow() {
  const debug = new FakeDebugger();
  const calls = {
    loadUrl: null,
    shown: 0,
    focused: 0,
    script: null,
    insertedText: null,
    inputEvents: []
  };
  const window = {
    async loadURL(url) {
      calls.loadUrl = url;
    },
    show() {
      calls.shown++;
    },
    focus() {
      calls.focused++;
    },
    webContents: {
      debugger: debug,
      getURL: () => "https://chatgpt.com/",
      session: {
        fetch: async () => new Response(JSON.stringify({ accessToken: "fixture-token" }), {
          status: 200,
          headers: { "content-type": "application/json" }
        })
      },
      executeJavaScript: async source => {
        calls.script = source;
        assert.doesNotThrow(() => new Function(`return ${source};`));
        return {
          focused: true,
          tagName: "DIV",
          contentEditable: "true"
        };
      },
      insertText: async text => {
        calls.insertedText = text;

        const postData = JSON.stringify({
          action: "next",
          model: "gpt-5-6-thinking",
          thinking_effort: "extended",
          parent_message_id: "client-created-root",
          client_prepare_state: "success",
          conversation_mode: {
            kind: "gizmo_interaction",
            gizmo_id: "g-p-fixture"
          },
          enable_message_followups: true,
          system_hints: [],
          supports_buffering: true,
          supported_encodings: ["v1"],
          model_response_contracts: [{ type: "photo_upload_action.v1" }],
          messages: [{
            metadata: { submission_mode: "manual_send" },
            content: { content_type: "text", parts: ["private prompt text"] }
          }]
        });

        debug.emit("message", {}, "Network.requestWillBeSentExtraInfo", {
          requestId: "request-1",
          headers: {
            "OpenAI-Sentinel-Chat-Requirements-Token": "secret-requirements-token",
            "OpenAI-Sentinel-Turnstile-Token": "secret-turnstile-token",
            "OpenAI-Sentinel-Proof-Token": "secret-proof-token",
            "OAI-Telemetry": "[1,2,3]",
            "x-conduit-token": "secret-conduit-token",
            "x-oai-is-client-observation": "v1.fixture-observation",
            "x-openai-web-frontend": "core_web",
            "oai-session-id": "session-fixture",
            "user-agent": "FixtureBrowser/1.0"
          }
        });
        debug.emit("message", {}, "Network.requestWillBeSent", {
          requestId: "request-1",
          request: {
            url: "https://chatgpt.com/backend-api/f/conversation",
            method: "POST",
            headers: {
              "content-type": "application/json"
            },
            postData
          },
          initiator: {
            type: "script",
            stack: {
              callFrames: [{
                functionName: "nativeSubmit",
                scriptId: "42",
                url: "https://chatgpt.com/cdn/assets/934244.fixture.js",
                lineNumber: 0,
                columnNumber: 35
              }]
            }
          }
        });
      },
      sendInputEvent: event => {
        calls.inputEvents.push(event);
      }
    }
  };

  return { window, debug, calls };
}

test("native trace captures the real request boundary without exposing security token values", async () => {
  const { window, debug, calls } = fakeWindow();

  const result = await traceModule.operations.traceNativeSend({
    window,
    args: { prompt: "trace fixture" },
    sleep: async () => {}
  });

  assert.equal(calls.loadUrl, "https://chatgpt.com/");
  assert.equal(calls.shown, 1);
  assert.equal(calls.focused, 1);
  assert.match(calls.script, /prompt editor/);
  assert.equal(calls.insertedText, "trace fixture");
  assert.deepEqual(calls.inputEvents, [
    { type: "keyDown", keyCode: "Enter" },
    { type: "keyUp", keyCode: "Enter" }
  ]);
  assert.doesNotMatch(calls.script, /send-button/);

  assert.equal(result.request.model, "gpt-5-6-thinking");
  assert.equal(result.request.thinkingEffort, "extended");
  assert.equal(result.request.clientPrepareState, "success");
  assert.equal(result.request.conversationMode, "gizmo_interaction");
  assert.equal(result.request.projectConversation, true);
  assert.equal(result.request.submissionMode, "manual_send");
  assert.deepEqual(result.request.modelResponseContracts, ["photo_upload_action.v1"]);

  assert.equal(
    result.headers["openai-sentinel-chat-requirements-token"].present,
    true
  );
  assert.equal(
    result.headers["openai-sentinel-turnstile-token"].length,
    "secret-turnstile-token".length
  );
  assert.equal(
    result.headers["x-openai-web-frontend"].value,
    "core_web"
  );
  assert.equal(result.initiator.type, "script");
  assert.deepEqual(result.initiator.frames[0], {
    functionName: "nativeSubmit",
    url: "https://chatgpt.com/cdn/assets/934244.fixture.js",
    line: 1,
    column: 36,
    scriptId: "42"
  });
  assert.match(result.initiator.sourceSnippets[0].snippet, /dispatchConversation/);

  const serialized = JSON.stringify(result);
  assert.doesNotMatch(serialized, /secret-requirements-token/);
  assert.doesNotMatch(serialized, /secret-turnstile-token/);
  assert.doesNotMatch(serialized, /secret-proof-token/);
  assert.doesNotMatch(serialized, /secret-conduit-token/);
  assert.doesNotMatch(serialized, /private prompt text/);

  assert.ok(debug.commands.some(value => value.method === "Network.enable"));
  assert.ok(debug.commands.some(value => value.method === "Debugger.enable"));
  assert.ok(debug.commands.some(value => value.method === "Debugger.getScriptSource"));
  assert.equal(debug.detached, true);
});

test("native trace authorization check uses the existing browser session", async () => {
  const { window } = fakeWindow();
  assert.equal(await traceModule.isAuthorized(window), true);
});
