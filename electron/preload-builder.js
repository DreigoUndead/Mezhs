const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { CHROME_RUNTIME_SHIM } = require('./browser-identity');

// Provider files are trusted application resources, not content from the web page.
function createBrowserPreload(modulePath, browserModule) {
  const pageFile = browserModule.pageOperationFile;
  if (!pageFile && Object.keys(browserModule.pageOperations || {}).length)
    throw new Error('Browser page operations require a renderer-safe pageOperationFile.');
  const pageSource = pageFile
    ? fs.readFileSync(pageFile === true ? path.resolve(modulePath) : path.resolve(path.dirname(modulePath), pageFile), 'utf8')
    : '';
  const source = [
    'const CHROME_RUNTIME_SHIM = ' + JSON.stringify(CHROME_RUNTIME_SHIM) + ';',
    'const pageOperations = (() => { const module = { exports: {} };',
    pageSource,
    'return module.exports.pageOperations || {}; })();',
    fs.readFileSync(path.join(__dirname, 'browser-preload.js'), 'utf8')
  ].join('\n');
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'mezhs-sandbox-preload-'));
  const preloadPath = path.join(directory, 'preload.js');
  const authPath = path.join(directory, 'auth-preload.js');
  const authSource = [
    'const { webFrame } = require("electron");',
    'if (process.sandboxed !== true) throw new Error("MEZS auth preload requires renderer sandbox.");',
    'void webFrame.executeJavaScript(' + JSON.stringify(CHROME_RUNTIME_SHIM) + ').catch(error => {',
    '  console.error("Chrome compatibility shim failed: " + String(error?.message ?? error));',
    '});'
  ].join('\n');
  try {
    fs.writeFileSync(preloadPath, source, 'utf8');
    fs.writeFileSync(authPath, authSource, 'utf8');
  } catch (error) {
    fs.rmSync(directory, { recursive: true, force: true });
    throw error;
  }
  return {
    path: preloadPath,
    authPath,
    cleanup: () => fs.rmSync(directory, { recursive: true, force: true })
  };
}

module.exports = { createBrowserPreload };
