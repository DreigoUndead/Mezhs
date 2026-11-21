const { ORIGIN } = require('./account-transport.js');

async function stopGeneration(context) {
  return context.page.invoke('stopGeneration', {}, 1200);
}

async function sendPrompt(context) {
  if (context.args.newChat) await context.window.loadURL(ORIGIN + '/');
  return context.page.invoke('sendPrompt', context.args, 190000);
}

module.exports = { sendPrompt, stopGeneration };
