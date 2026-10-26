const ORIGIN = "https://chatgpt.com";
const accountIds = new WeakMap();
const API = Object.freeze({
  session: "/api/auth/session",
  projects: "/backend-api/gizmos/snorlax/sidebar",
  models: "/backend-api/models?history_and_training_disabled=false",
  modelPreference: "/backend-api/settings/user_last_used_model_config",
  conversation: "/backend-api/f/conversation",
  conversationPrepare: "/backend-api/f/conversation/prepare",
  conversationInit: "/backend-api/conversation/init",
  conversationById: id => `/backend-api/conversation/${encodeURIComponent(id)}`,
  files: "/backend-api/files",
  processFileUpload: "/backend-api/files/process_upload_stream",
  fileDownload: id => `/backend-api/files/${encodeURIComponent(id)}/download`
});

async function webApiHeaders(session, endpoint, clientSessionId, extra = {}) {
  const headers = {
    "Oai-Language": "en-US",
    "Oai-Session-Id": clientSessionId,
    "x-openai-web-frontend": "core_web",
    "x-openai-target-path": endpoint,
    "x-openai-target-route": endpoint,
    ...extra
  };
  const deviceId = (
    await session.cookies.get({ url: ORIGIN, name: "oai-did" })
  )[0]?.value;
  if (deviceId) headers["Oai-Device-Id"] = deviceId;
  return headers;
}

function clientContext() {
  return {
    app_name: "chatgpt.com",
    has_web_push_capabilities: false,
    web_push_notification_permission: "default"
  };
}

async function nativeChatRequirementsHeaders(window, session, token) {
  const accountId = accountIds.get(session) || null;
  const script = `
    (async () => {
      const providerCacheKey = Symbol.for("mezhs.chatgpt.nativeRequirementsProvider");
      const resources = performance.getEntriesByType("resource");
      const runtimeUrls = resources
        .map(entry => String(entry.name || ""))
        .filter(url => /^https:\\/\\/chatgpt\\.com\\/cdn\\/assets\\/[^/?#]+\\.js(?:[?#].*)?$/i.test(url));

      // Resource Timing can be cleared or truncated without unloading the page.
      // Keep only the verified function for this renderer document, not credentials
      // or challenge results. A real page navigation resets globalThis.
      let provider = globalThis[providerCacheKey];
      if (typeof provider !== "function") {
        const preferred = runtimeUrls.filter(url =>
          /\\/(?:manifest|entry\\.client|root)-[^/?#]*\\.js(?:[?#]|$)/i.test(url));
        const candidates = [...new Set([...preferred, ...runtimeUrls.slice().reverse()])];
        for (const runtimeUrl of candidates) {
          try {
            const runtime = await import(runtimeUrl);
            const moduleCache = runtime?.__webpack_require__?.c;
            const knownCandidate =
              moduleCache?.lXEa?.exports?.f ?? moduleCache?.lXE?.exports?.f;
            if (typeof knownCandidate === "function" && knownCandidate.length === 1) {
              provider = knownCandidate;
            } else {
              const semanticCandidates = [...new Set(
                Object.values(moduleCache || {})
                  .map(record => record?.exports?.f)
                  .filter(candidate => {
                    if (typeof candidate !== "function" || candidate.length !== 1)
                      return false;
                    const source = Function.prototype.toString.call(candidate);
                    return ["chatRequirements", "proofToken", "requirementsKey", "turnstileToken"]
                      .every(marker => source.includes(marker));
                  })
              )];
              if (semanticCandidates.length === 1)
                provider = semanticCandidates[0];
            }
            if (typeof provider !== "function") continue;
            globalThis[providerCacheKey] = provider;
            break;
          } catch {
            // A loaded asset may not be importable as a module. Keep looking.
          }
        }
      }
      if (typeof provider !== "function" || provider.length !== 1) {
        throw new Error(runtimeUrls.length
          ? "ChatGPT native requirements provider export is unavailable " +
            "(runtimeCandidates=" + runtimeUrls.length + ")."
          : "ChatGPT native requirements provider was not observed in this document " +
            "(resourceEntries=" + resources.length + ", cachedProvider=false).");
      }

      const prepared = await provider(async requirementsKey => {
        const response = await fetch("/backend-api/sentinel/chat-requirements/prepare", {
          method: "POST",
          credentials: "include",
          headers: {
            "Authorization": "Bearer " + ${JSON.stringify(token)},
            ...(${JSON.stringify(accountId)} ? {
              "ChatGPT-Account-Id": ${JSON.stringify(accountId)}
            } : {}),
            "Content-Type": "application/json",
            "OAI-Language": "en-US"
          },
          body: JSON.stringify({ p: requirementsKey })
        });
        if (!response.ok)
          throw new Error("ChatGPT requirements prepare failed with HTTP " + response.status);
        return response.json();
      });
      return {
        forceLogin: prepared?.chatRequirements?.force_login === true,
        headers: prepared?.headers || null
      };
    })()
  `;
  const result = await window.webContents.executeJavaScript(script, true);
  if (result?.forceLogin)
    throw new Error("ChatGPT native chat requirements require login.");

  const allowed = new Set([
    "openai-sentinel-chat-requirements-token",
    "openai-sentinel-chat-requirements-prepare-token",
    "openai-sentinel-proof-token",
    "openai-sentinel-turnstile-token"
  ]);
  const headers = {};
  for (const [name, value] of Object.entries(result?.headers || {})) {
    if (!allowed.has(name.toLowerCase())) continue;
    if (typeof value !== "string" || /[\r\n]/.test(value))
      throw new Error("ChatGPT native requirements returned an invalid header.");
    if (value) headers[name] = value;
  }
  if (!Object.keys(headers).some(name =>
    name.toLowerCase() === "openai-sentinel-chat-requirements-token" ||
    name.toLowerCase() === "openai-sentinel-chat-requirements-prepare-token")) {
    throw new Error("ChatGPT native chat requirements did not produce a requirements token.");
  }
  return headers;
}

async function accessToken(session) {
  const response = await session.fetch(ORIGIN + API.session, {
    credentials: "include",
    cache: "no-store"
  });
  if (!response.ok) {
    accountIds.delete(session);
    return null;
  }

  const auth = await response.json();
  const accountId = String(
    auth?.account?.id ||
    auth?.accountId ||
    auth?.account_id ||
    auth?.user?.id ||
    ""
  ).trim();
  if (accountId) accountIds.set(session, accountId);
  else accountIds.delete(session);
  return auth?.accessToken || null;
}

async function requireToken(session) {
  const token = await accessToken(session);
  if (!token) throw new Error("ChatGPT authorization is required.");
  return token;
}

const INTEGRITY_STATE_COOKIE = "__Secure-oai-is";
const INTEGRITY_STATE_PATTERN =
  /^ois1\.[A-Za-z0-9_-]+\.([A-Za-z0-9_-]{16})\.[A-Za-z0-9_-]+$/;
const INTEGRITY_STATE_MAX_AGE_SECONDS = 30 * 24 * 60 * 60;

async function apiFetch(session, token, endpoint, options = {}) {
  const accountId = accountIds.get(session);
  const headers = {
    Authorization: `Bearer ${token}`,
    ...(accountId ? { "ChatGPT-Account-Id": accountId } : {}),
    ...(options["headers"] || {})
  };
  headers["X-OAI-IS-Client-Observation"] =
    await integrityStateObservation(session);

  const response = await session.fetch(ORIGIN + endpoint, {
    ...options,
    headers,
    credentials: "include",
    cache: "no-store"
  });
  await applyIntegrityStateUpdate(session, response.headers);

  if (response.ok) return response;

  let providerCode = null;
  try {
    const error = await response.json();
    const code = error?.detail?.code ?? error?.code;
    if (typeof code === "string" && /^[a-z0-9_-]{1,80}$/i.test(code))
      providerCode = code;
  } catch {
    // Non-JSON errors still retain HTTP status without retaining private response bodies.
  }
  throw Object.assign(
    new Error(`ChatGPT ${endpoint} failed with HTTP ${response.status}`),
    {
      status: response.status,
      endpoint,
      providerCode,
      retryAfterMs: parseRetryAfterMs(response.headers.get("retry-after"))
    }
  );
}

async function integrityStateObservation(session) {
  try {
    const cookies = await session.cookies.get({
      url: ORIGIN,
      name: INTEGRITY_STATE_COOKIE
    });
    const value = String(cookies?.[0]?.value || "").trim();
    if (!value) return "v1.r.m";
    const match = INTEGRITY_STATE_PATTERN.exec(value);
    return match
      ? `v1.r.p.${match[1]}`
      : "v1.r.i";
  } catch {
    return "v1.r.r";
  }
}

async function applyIntegrityStateUpdate(session, headers) {
  const update = String(headers?.get?.("x-oai-is-update") || "").trim();
  if (!INTEGRITY_STATE_PATTERN.test(update) || !session.cookies?.set)
    return;

  await session.cookies.set({
    url: ORIGIN,
    name: INTEGRITY_STATE_COOKIE,
    value: update,
    path: "/",
    secure: true,
    sameSite: "lax",
    expirationDate: Date.now() / 1000 + INTEGRITY_STATE_MAX_AGE_SECONDS
  });
}

function parseRetryAfterMs(value) {
  if (!value) return null;
  const seconds = Number(value);
  if (Number.isFinite(seconds) && seconds >= 0)
    return Math.ceil(seconds * 1000);
  const date = Date.parse(value);
  return Number.isNaN(date) ? null : Math.max(0, date - Date.now());
}

function isConversationUnavailable(error, conversationId) {
  return error?.status === 404 &&
    error?.endpoint === API.conversationById(conversationId) &&
    error?.providerCode === "conversation_inaccessible";
}

async function apiJson(session, token, endpoint, options = {}) {
  const response = await apiFetch(session, token, endpoint, options);
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

module.exports = { ORIGIN, API, accessToken, requireToken, webApiHeaders, clientContext, nativeChatRequirementsHeaders, apiFetch, apiJson, isConversationUnavailable, parseRetryAfterMs };
