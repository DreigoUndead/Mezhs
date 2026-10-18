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
  }
};
