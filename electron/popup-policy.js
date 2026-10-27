// Authentication destinations are exact HTTPS origins documented by the providers.
// Unknown OAuth/enterprise-SSO redirects fail closed until their chain is verified.
const authOrigins = new Map([
  ["https://chatgpt.com", [
    "https://chatgpt.com",
    "https://auth.openai.com",
    "https://accounts.google.com",
    "https://appleid.apple.com",
    "https://login.microsoftonline.com",
    "https://login.live.com"
  ]],
  ["https://grok.com", [
    "https://grok.com",
    "https://accounts.x.ai",
    "https://auth.x.ai",
    "https://auth.grok.com",
    "https://x.com",
    "https://accounts.google.com",
    "https://appleid.apple.com"
  ]],
  ["https://gemini.google.com", [
    "https://gemini.google.com",
    "https://accounts.google.com"
  ]]
]);

function parsedOrigin(url) {
  try {
    const parsed = new URL(url);
    if (parsed.protocol !== "https:" || parsed.username || parsed.password)
      return null;
    return parsed.origin;
  } catch {
    return null;
  }
}

function popupAllowed(providerHomeUrl, targetUrl) {
  const origins = authOrigins.get(parsedOrigin(providerHomeUrl));
  return Boolean(origins && origins.includes(parsedOrigin(targetUrl)));
}

function attachPopupPolicy(contents, providerHomeUrl, persistentSession, authPreload) {
  const allowed = url => popupAllowed(providerHomeUrl, url);
  contents.setWindowOpenHandler(({ url }) => {
    if (!allowed(contents.getURL()) || !allowed(url))
      return { action: "deny" };

    return {
      action: "allow",
      overrideBrowserWindowOptions: {
        webPreferences: {
          session: persistentSession,
          preload: authPreload,
          sandbox: true,
          contextIsolation: true,
          nodeIntegration: false
        }
      }
    };
  });

  contents.on("did-create-window", child => {
    const blockUnexpectedNavigation = (event, navigation) => {
      const url = typeof navigation === "string" ? navigation : navigation?.url;
      if (navigation?.isMainFrame === false) return;
      if (!allowed(url)) event.preventDefault();
    };
    child.webContents.on("will-navigate", blockUnexpectedNavigation);
    child.webContents.on("will-redirect", blockUnexpectedNavigation);
    // OAuth consent pages can create another window; children inherit the same boundary.
    attachPopupPolicy(child.webContents, providerHomeUrl, persistentSession, authPreload);
  });
}

module.exports = { popupAllowed, attachPopupPolicy };
