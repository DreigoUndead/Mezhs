const { ORIGIN } = require('./account-transport.js');

async function sendPrompt(context) {
  if (context.args.newChat) await context.window.loadURL(ORIGIN + '/');
  return context.page.invoke('sendPrompt', context.args);
}

module.exports = { sendPrompt };
