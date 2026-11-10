'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const { rspackRegistryShapePage, invokeDiagnostic } = require('../electron/diagnostics');

const schema = [
  'scannedGlobals', 'matchingNames', 'dataArrayCandidates', 'accessorCandidates',
  'ownPushFunctions', 'inheritedOrAbsentPush', 'nativeLookingPush', 'nonNativePush',
  'emptyArrays', 'processedEntryShapeCandidates', 'noProcessedEntryShape',
  'passedNameAndArray', 'passedNameArrayAndOwnPush',
  'passedNameArrayOwnPushNonNative', 'passedAllPredicates',
  'descriptorErrors', 'limitExceeded', 'incomplete'
].sort();
const look = globals => rspackRegistryShapePage(globals);
const runtime = (entries = [[[1], { a: function factory() {} }]], push) => {
  const array = entries.slice();
  array.push = push || function customRuntimePush() {
    throw Error('registry push must never run');
  };
  return array;
};
const expectSchema = result => {
  assert.deepEqual(Object.keys(result).sort(), schema);
  for (const key of schema) {
    if (key === 'incomplete' || key === 'limitExceeded')
      assert.equal(typeof result[key], 'boolean');
    else assert.ok(Number.isSafeInteger(result[key]) && result[key] >= 0, key);
  }
};
const expectStages = result => {
  assert.ok(result.matchingNames >= result.passedNameAndArray);
  assert.ok(result.passedNameAndArray >= result.passedNameArrayAndOwnPush);
  assert.ok(result.passedNameArrayAndOwnPush >= result.passedNameArrayOwnPushNonNative);
  assert.ok(result.passedNameArrayOwnPushNonNative >= result.passedAllPredicates);
};

test('empty and non-runtime globals have a complete bounded schema', () => {
  for (const root of [{}, { webpackChunkA: {}, irrelevant: 'private-token' }]) {
    const found = look(root);
    expectSchema(found);
    expectStages(found);
    assert.equal(found.incomplete, false);
    assert.equal(found.passedAllPredicates, 0);
    assert.equal(found.scannedGlobals, Object.keys(root).length);
  }
});

test('missing and inherited push do not qualify as own functions', () => {
  const absent = [[[1], {}]];
  const inherited = [[[1], {}]];
  Object.setPrototypeOf(inherited, { push: function inheritedPush() {} });
  const found = look({ webpackChunkMissing: absent, webpackChunkInherited: inherited });
  expectSchema(found);
  expectStages(found);
  assert.equal(found.inheritedOrAbsentPush, 2);
  assert.equal(found.passedNameAndArray, 2);
  assert.equal(found.passedNameArrayAndOwnPush, 0);
});

test('native-looking, bound and ordinary push functions are classified without calls', () => {
  const ordinary = runtime();
  const bound = runtime([], function original() {}.bind(null));
  const native = runtime([], Array.prototype.push);
  const found = look({ webpackChunkOrdinary: ordinary, webpackChunkBound: bound,
    webpackChunkNative: native });
  expectSchema(found);
  expectStages(found);
  assert.equal(found.nativeLookingPush, 2);
  assert.equal(found.nonNativePush, 1);
  assert.equal(found.passedAllPredicates, 1);
  assert.equal(found.noProcessedEntryShape, 0);
});

test('valid shape, decoys and malformed chunk entries are classified without push or writes', () => {
  let writes = 0;
  const candidate = new Proxy(runtime([null, [[9], { f: () => {} }]]), {
    set() { writes++; throw Error('unexpected write'); }
  });
  const found = look({ webpackChunkValid: candidate, webpackChunkDecoy: runtime([[]]),
    webpackChunkEmpty: runtime([]), otherChunk: runtime() });
  expectSchema(found);
  expectStages(found);
  assert.equal(found.passedAllPredicates, 1);
  assert.equal(found.processedEntryShapeCandidates, 1);
  assert.equal(found.emptyArrays, 1);
  assert.equal(found.noProcessedEntryShape, 2);
  assert.equal(writes, 0);
});

test('accessor globals and accessor push are classified without running their getters', () => {
  let getters = 0;
  const root = {};
  Object.defineProperty(root, 'webpackChunkAccessor', { get() { getters++; throw Error('secret'); } });
  const array = runtime();
  Object.defineProperty(array, 'push', { configurable: true, get() {
    getters++; throw Error('secret');
  } });
  root.webpackChunkArray = array;
  const found = look(root);
  expectSchema(found);
  assert.equal(found.accessorCandidates, 2);
  assert.equal(found.passedNameAndArray, 1);
  assert.equal(found.passedNameArrayAndOwnPush, 0);
  assert.equal(found.incomplete, false);
  assert.equal(getters, 0);
});

test('Array.prototype.push getter is not invoked during page inspection', () => {
  const context = vm.createContext({});
  vm.runInContext(`
    globalThis.webpackChunkExample = [[[1], { a: function factory() {} }]];
    globalThis.webpackChunkExample.push = function runtimePush() {};
    globalThis.getterCalls = 0;
    Object.defineProperty(Array.prototype, 'push', {
      configurable: true,
      get() { globalThis.getterCalls++; throw new Error('unexpected getter'); }
    });
  `, context);
  const found = vm.runInContext('(' + rspackRegistryShapePage.toString() + ')()', context);
  expectSchema(found);
  expectStages(found);
  assert.equal(context.getterCalls, 0);
  assert.equal(found.incomplete, false);
  assert.equal(found.passedAllPredicates, 1);
});

test('forged page toString illustrates explicitly accepted best-effort limitation', () => {
  const context = vm.createContext({});
  vm.runInContext(`
    globalThis.webpackChunkExample = [[[1], { a: function factory() {} }]];
    globalThis.webpackChunkExample.push = function runtimePush() {};
    globalThis.introspectionCalls = 0;
    Function.prototype.toString = function forged() {
      globalThis.introspectionCalls++;
      return 'function fake() { [native code] }';
    };
  `, context);
  const found = vm.runInContext('(' + rspackRegistryShapePage.toString() + ')()', context);
  expectSchema(found);
  // Not a security assertion: same-realm inspection cannot authenticate its built-ins.
  assert.equal(context.introspectionCalls, 1);
  assert.equal(found.nativeLookingPush, 1);
  assert.equal(found.passedAllPredicates, 0);
  assert.equal(found.incomplete, false);
});

test('Proxy trap failure and revoked Proxy fail closed with no partial successful counts', () => {
  const throwing = new Proxy(runtime(), {
    getOwnPropertyDescriptor() { throw Error('private-token secret URL'); }
  });
  const revoked = Proxy.revocable(runtime(), {});
  revoked.revoke();
  for (const candidate of [throwing, revoked.proxy]) {
    const found = look({ webpackChunkBad: candidate });
    expectSchema(found);
    assert.equal(found.incomplete, true);
    assert.equal(found.descriptorErrors, 1);
    assert.equal(found.passedAllPredicates, 0);
    assert.equal(found.matchingNames, 0);
    assert.doesNotMatch(JSON.stringify(found), /private-token|secret|URL|stack|webpackChunkBad/);
  }
});

test('bounds fail closed with fixed schema rather than truncated successful observations', () => {
  const cases = [];
  const globals = {};
  for (let i = 0; i < 5001; i++) globals['unrelated' + i] = true;
  cases.push(globals);
  const matching = {};
  for (let i = 0; i < 33; i++) matching['webpackChunk' + i] = runtime();
  cases.push(matching);
  cases.push({ webpackChunkLarge: runtime(Array(2049).fill(null)) });
  cases.push({ webpackChunkA: runtime(Array(2048).fill(null)),
    webpackChunkB: runtime(Array(2048).fill(null)),
    webpackChunkC: runtime(Array(2048).fill(null)) });
  for (const root of cases) {
    const found = look(root);
    expectSchema(found);
    assert.equal(found.incomplete, true);
    assert.equal(found.limitExceeded, true);
    assert.equal(found.passedAllPredicates, 0);
    assert.equal(found.matchingNames, 0);
    assert.equal(found.descriptorErrors, 0);
  }
});

test('global enumeration introspection failure is sanitized', () => {
  const root = new Proxy({}, { ownKeys() { throw Error('Bearer private-token'); } });
  const found = look(root);
  expectSchema(found);
  assert.equal(found.incomplete, true);
  assert.equal(found.descriptorErrors, 1);
  assert.doesNotMatch(JSON.stringify(found), /Bearer|private-token|stack/);
});

test('opt-in and ChatGPT-only checks execute before crossing to renderer', async () => {
  let calls = 0;
  let script;
  const window = { webContents: { executeJavaScript(value) {
    calls++;
    script = value;
    return vm.runInNewContext(value, { webpackChunkchatgpt: runtime() });
  } } };
  for (const args of [{}, { inspect: false }, { inspect: 'true' }]) {
    await assert.rejects(invokeDiagnostic('rspackRegistryShape', args,
      { window, browserModule: { name: 'ChatGPT' } }), /inspect=true/);
  }
  await assert.rejects(invokeDiagnostic('rspackRegistryShape', { inspect: true },
    { window, browserModule: { name: 'Grok' } }), /limited to ChatGPT/);
  assert.equal(calls, 0);
  const found = await invokeDiagnostic('rspackRegistryShape', { inspect: true },
    { window, browserModule: { name: 'ChatGPT' } });
  assert.equal(calls, 1);
  expectSchema(found);
  assert.equal(found.passedAllPredicates, 1);
  assert.match(script, /function rspackRegistryShapePage/);
  assert.doesNotMatch(script, /\.push\(|Reflect\.apply|clearResourceTimings|prepareRequirements/);
});

test('operation is confined to existing authenticated loopback diagnostic path', () => {
  const host = fs.readFileSync('src/Mezhs.Integration.Browser/BrowserDiagnosticsModule.cs', 'utf8');
  const api = fs.readFileSync('src/Mezhs.Api/MezhsApi.cs', 'utf8');
  assert.match(host, /"rspackRegistryShape"/);
  assert.match(host, /session\.UseAuthorizedAsync/);
  assert.match(api, /IPAddress\.IsLoopback/);
  assert.match(api, /integration\.Diagnostics\.InvokeAsync/);
  assert.equal((api.match(/\/v1\/diagnostics\/connections\/\{connectionId\}\/browser\/\{operation\}/g) || []).length, 1);
});
