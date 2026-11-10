'use strict';
// Controlled transport fixture: run the real Electron diagnostic dispatcher
// against a synthetic renderer. No Electron process or external login needed.
const vm = require('node:vm');
const { invokeDiagnostic } = require('../electron/diagnostics');

const provider = process.argv[2];
const args = JSON.parse(process.argv[3] || '{}');
const context = vm.createContext({});
vm.runInContext(
  'globalThis.webpackChunkDiagnostic = [[[1], { a: function factory() {} }]];' +
  'globalThis.webpackChunkDiagnostic.push = function runtimePush() {};',
  context
);
let rendererCalls = 0;
const window = { webContents: { executeJavaScript(source) {
  rendererCalls++;
  return vm.runInContext(source, context);
} } };

(async () => {
  try {
    const result = await invokeDiagnostic('rspackRegistryShape', args, {
      window, browserModule: { name: provider }
    });
    process.stdout.write(JSON.stringify({ ok: true, result, rendererCalls }));
  } catch (error) {
    process.stdout.write(JSON.stringify({
      ok: false, error: String(error.message), rendererCalls
    }));
  }
})().catch(error => {
  process.stderr.write(String(error.stack || error));
  process.exitCode = 1;
});
