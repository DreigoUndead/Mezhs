const { ORIGIN } = require('./account-transport.js');
const PROMPT_EDITOR_SELECTOR = [
  '#prompt-textarea',
  '#mobile-composer-prompt',
  '[data-mobile-composer-prompt]',
  'textarea[name="prompt-textarea"]',
  '[data-testid="prompt-textarea"]',
  '.ProseMirror[contenteditable="true"]',
  '[contenteditable="true"][role="textbox"]',
  '[contenteditable="true"][data-virtualkeyboard="true"]'
].join(', ');

async function sendPrompt({ window, args }) {
      const submitOnce = async () => {
        if (args.newChat) await window.loadURL(ORIGIN + '/');
        const prompt = JSON.stringify(String(args.prompt || ""));
        const promptEditorSelector = JSON.stringify(PROMPT_EDITOR_SELECTOR);
        return window.webContents.executeJavaScript(`
          (async () => {
            // Observe the guest frontend's existing authorization response without creating challenges ourselves.
            // force_login explicitly ends this attempt; a missing field is not permission or a diagnosis.
            const originalFetch = globalThis.fetch;
            let guestLoginRequired = false;
            const observedFetch = function(...fetchArgs) {
              const pending = originalFetch.apply(this, fetchArgs);
              const url = String(fetchArgs[0]?.url || fetchArgs[0] || '');
              if (url.includes('/unauth-mweb/sentinel/chat-requirements/finalize')) {
                pending.then(response => response.clone().json()).then(result => {
                  if (result?.force_login === true) guestLoginRequired = true;
                }).catch(() => {});
              }
              return pending;
            };
            if (typeof originalFetch === 'function') globalThis.fetch = observedFetch;
            try {
              const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
              const legacySelector = '[data-message-author-role="assistant"]';
              const octaneSelector = '[data-conversation-transcript] [data-message-role="assistant"]';
              const legacyBefore = document.querySelectorAll(legacySelector).length;
              const octaneBefore = document.querySelectorAll(octaneSelector).length;
              const userSelector = '[data-conversation-transcript] [data-message-role=user], [data-message-author-role=user]';
              const usersBefore = document.querySelectorAll(userSelector).length;
              let editor = null;
              for (let i = 0; i < 120 && !editor; i++) {
                editor = document.querySelector(${promptEditorSelector});
                if (!editor) await sleep(250);
              }
              if (!editor) return { ok: false, error: 'ChatGPT prompt editor was not found.' };
              editor.focus();
              if (editor.tagName === 'TEXTAREA' || editor.tagName === 'INPUT') {
                const prototype = editor.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set;
                if (setter) setter.call(editor, ${prompt});
                else editor.value = ${prompt};
                editor.dispatchEvent(new Event('input', { bubbles: true }));
              } else {
                document.execCommand('selectAll', false, null);
                document.execCommand('insertText', false, ${prompt});
                editor.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: ${prompt} }));
              }
              let send = null;
              for (let i = 0; i < 360; i++) {
                if (guestLoginRequired)
                  return { ok: false, errorCode: 'guest_login_required', error: 'ChatGPT requires login before continuing as a guest.' };
                send = editor.closest('form')?.querySelector('button[aria-label="Send message"]')
                  || document.querySelector('button[data-testid="send-button"], button[aria-label="Send prompt"], button[aria-label="Send message"]');
                if (send && !send.disabled && send.getAttribute('aria-disabled') !== 'true') break;
                await sleep(250);
              }
              if (guestLoginRequired)
                return { ok: false, errorCode: 'guest_login_required', error: 'ChatGPT requires login before continuing as a guest.' };
              if (!send || send.disabled || send.getAttribute('aria-disabled') === 'true')
                return { ok: false, error: 'ChatGPT send button did not become available.' };
              send.click();
              let last = '';
              let stable = 0;
              let emptyCompletedPolls = 0;
              let unsubmittedPolls = 0;
              while (true) {
                if (guestLoginRequired)
                  return { ok: false, errorCode: 'guest_login_required', error: 'ChatGPT requires login before continuing as a guest.' };
                const octaneMessages = document.querySelectorAll(octaneSelector);
                const octane = octaneMessages[octaneMessages.length - 1];
                if (octaneMessages.length > octaneBefore && octane?.hasAttribute('data-message-complete')) {
                  // Octane can set the completion marker before its reply content is rendered.
                  const content = octane.children[1]?.innerText?.trim() || '';
                  if (content) return { ok: true, text: content };
                  // Completion can precede rendering, but a permanently blank reply must not block the agent.
                  if (++emptyCompletedPolls >= 10)
                    return { ok: false, errorCode: 'empty_guest_reply', error: 'ChatGPT completed the guest reply without readable text.' };
                }
                const messages = document.querySelectorAll(legacySelector);
                const text = messages[messages.length - 1]?.innerText?.trim() || '';
                const stop = document.querySelector('button[data-testid="stop-button"], button[aria-label="Stop streaming"]');
                stable = text && text === last ? stable + 1 : 0;
                last = text;
                if (messages.length > legacyBefore && text && !stop && stable >= 6)
                  return { ok: true, text };
                // A clickable send button does not guarantee a guest turn was accepted.
                if (unsubmittedPolls >= 0) {
                  if (octaneMessages.length > octaneBefore || messages.length > legacyBefore
                      || document.querySelectorAll(userSelector).length > usersBefore)
                    unsubmittedPolls = -1;
                  else if (++unsubmittedPolls >= 60)
                    return { ok: false, errorCode: 'guest_submission_timeout', error: 'ChatGPT did not start a guest conversation after submission.' };
                }
                await sleep(500);
              }
            } finally {
              if (globalThis.fetch === observedFetch) globalThis.fetch = originalFetch;
            }
          })()
        `, true);
      };
      const first = await submitOnce();
      if (first.ok || first.errorCode !== 'empty_guest_reply' || !args.newChat)
        return first;
      // Only retry a new chat; replaying an existing conversation can duplicate actions.
      const second = await submitOnce();
      if (!second.ok && second.errorCode === 'empty_guest_reply')
        return { ...second, error: second.error + ' Fresh-conversation retry also returned an empty reply.' };
      return second;
}

module.exports = { sendPrompt };
