const { ORIGIN, API, accessToken, requireToken, apiJson } = require('./account-transport.js');
const { nativePickerModels } = require('./account-models.js');
const { sendAccountMessage } = require('./account-messages.js');
const { sendPrompt } = require('./guest.js');

module.exports = {
  name: "ChatGPT",
  homeUrl: ORIGIN + "/",

  async isAuthorized(window) {
    return Boolean(await accessToken(window.webContents.session).catch(() => null));
  },

  operations: {
    async getProjects({ session }) {
      const token = await requireToken(session);
      const projects = [];
      let cursor = null;
      do {
        const url = new URL(API.projects, ORIGIN);
        url.searchParams.set("conversations_per_gizmo", "0");
        if (cursor) url.searchParams.set("cursor", cursor);
        const page = await apiJson(session, token, url.pathname + url.search);
        for (const item of page?.items || []) {
          const project = item?.gizmo?.gizmo || item?.gizmo;
          const id = String(project?.id || "");
          const name = String(project?.display?.name || "").trim();
          if (id.startsWith("g-p-") && name) projects.push({ id, name });
        }
        cursor = typeof page?.cursor === "string" && page.cursor ? page.cursor : null;
      } while (cursor);
      return projects;
    },

    async getModels({ session }) {
      const token = await requireToken(session);
      const response = await apiJson(session, token, API.models);
      return nativePickerModels(response);
    },

    newChat(context) {
      return sendAccountMessage(context, true);
    },

    send(context) {
      return sendAccountMessage(context, false);
    },

    sendPrompt
  },

  pageOperations: {
    async sendPrompt({ args, sleep }) {
      const fail = (errorCode, error, submissionAttempted = false) =>
        ({ ok: false, errorCode, error, submissionAttempted });
      // A visible login wall is evidence; the ordinary sign-in button in the header is not.
      const loginRequired = () => [...document.querySelectorAll(
        '[role="dialog"], [role="alertdialog"], [data-testid="login-required"]'
      )].some(element =>
        /log in|sign in|sign up|create an account/i.test(element.innerText || '') &&
        /continue|chat|send|start/i.test(element.innerText || '')
      );
      const loginFailure = attempted => fail('guest_login_required',
        'ChatGPT visibly requires login before continuing as a guest.', attempted);
      const editorSelector = [
        '#prompt-textarea', '#mobile-composer-prompt', '[data-mobile-composer-prompt]',
        'textarea[name="prompt-textarea"]', '[data-testid="prompt-textarea"]',
        '.ProseMirror[contenteditable="true"]', '[contenteditable="true"][role="textbox"]',
        '[contenteditable="true"][data-virtualkeyboard="true"]'
      ].join(', ');
      const legacySelector = '[data-message-author-role="assistant"]';
      const octaneSelector = '[data-conversation-transcript] [data-message-role="assistant"]';
      const userSelector = '[data-conversation-transcript] [data-message-role="user"], [data-message-author-role="user"]';
      const legacyBefore = document.querySelectorAll(legacySelector).length;
      const octaneBefore = document.querySelectorAll(octaneSelector).length;
      const usersBefore = document.querySelectorAll(userSelector).length;

      let editor = null;
      for (let i = 0; i < 120 && !editor; i++) {
        if (loginRequired()) return loginFailure(false);
        editor = document.querySelector(editorSelector);
        if (!editor) await sleep(250);
      }
      if (!editor) return fail('guest_editor_unavailable', 'ChatGPT prompt editor was not found.');

      const prompt = String(args.prompt || '');
      editor.focus();
      if (editor.tagName === 'TEXTAREA' || editor.tagName === 'INPUT') {
        const prototype = editor.tagName === 'TEXTAREA'
          ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set;
        if (setter) setter.call(editor, prompt);
        else editor.value = prompt;
        editor.dispatchEvent(new Event('input', { bubbles: true }));
      } else {
        document.execCommand('selectAll', false, null);
        if (!document.execCommand('insertText', false, prompt))
          return fail('guest_prompt_insertion_failed', 'ChatGPT rejected prompt insertion.');
        editor.dispatchEvent(new InputEvent('input', {
          bubbles: true, inputType: 'insertText', data: prompt
        }));
      }

      let send = null;
      const enabled = button => button && !button.disabled &&
        button.getAttribute('aria-disabled') !== 'true';
      for (let i = 0; i < 120; i++) {
        if (loginRequired()) return loginFailure(false);
        send = editor.closest('form')?.querySelector('button[aria-label="Send message"]') ||
          document.querySelector('button[data-testid="send-button"], button[aria-label="Send prompt"], button[aria-label="Send message"]');
        if (enabled(send)) break;
        await sleep(250);
      }
      if (loginRequired()) return loginFailure(false);
      if (!enabled(send))
        return fail('guest_send_unavailable', 'ChatGPT send button did not become available.');

      // One click per requested turn. An ambiguous outcome must never replay the prompt.
      send.click();
      let lastText = '';
      let stable = 0;
      let blankCompleted = 0;
      let unsubmitted = 0;
      let submissionObserved = false;
      for (let i = 0; i < 240; i++) {
        if (loginRequired()) return loginFailure(true);
        const octaneMessages = document.querySelectorAll(octaneSelector);
        const octane = octaneMessages[octaneMessages.length - 1];
        const legacyMessages = document.querySelectorAll(legacySelector);
        const legacy = legacyMessages[legacyMessages.length - 1];
        submissionObserved ||= octaneMessages.length > octaneBefore ||
          legacyMessages.length > legacyBefore ||
          document.querySelectorAll(userSelector).length > usersBefore;

        if (octaneMessages.length > octaneBefore && octane?.hasAttribute('data-message-complete')) {
          // Octane may mark completion before the rendered reply text is available.
          const content = octane.children[1]?.innerText?.trim() || '';
          if (content) return { ok: true, text: content, submissionAttempted: true };
          if (++blankCompleted >= 10)
            return fail('empty_guest_reply', 'ChatGPT completed the guest reply without readable text.', true);
        }
        const text = legacy?.innerText?.trim() || '';
        const stop = document.querySelector('button[data-testid="stop-button"], button[aria-label="Stop streaming"]');
        stable = text && text === lastText ? stable + 1 : 0;
        lastText = text;
        if (legacyMessages.length > legacyBefore && text && !stop && stable >= 6)
          return { ok: true, text, submissionAttempted: true };

        if (!submissionObserved && ++unsubmitted >= 60)
          return fail('guest_submission_timeout',
            'Clicked Send, but ChatGPT did not show a new guest turn; delivery is uncertain.', true);
        await sleep(500);
      }
      return fail('guest_reply_timeout',
        'ChatGPT guest reply did not complete within the observation limit; delivery is uncertain.', true);
    }
  }
};
