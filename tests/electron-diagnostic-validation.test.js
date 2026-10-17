const test = require('node:test');
const assert = require('node:assert/strict');
const { invokeDiagnostic } = require('../electron/diagnostics');

function harness() {
  const calls = [];
  const context = {
    browserModule: { name: 'Synthetic' },
    window: { webContents: {
      executeJavaScript: async source => { calls.push(source); return { matched: true }; },
      sendInputEvent: event => calls.push(event)
    } },
    sleep: async () => {}
  };
  return { context, calls };
}

test('Point diagnostics reject missing, coerced, and non-finite coordinates before renderer interaction', async () => {
  const invalid = [
    {}, { x: null, y: 4 }, { x: 4, y: null },
    { x: '4', y: 5 }, { x: true, y: 5 },
    { x: 4, y: NaN }, { x: Infinity, y: 5 }
  ];
  for (const operation of ['inspectPoint', 'click']) {
    for (const args of invalid) {
      const { context, calls } = harness();
      await assert.rejects(invokeDiagnostic(context, operation, args), /finite x and y coordinates/);
      assert.equal(calls.length, 0, operation + ' interacted with renderer on invalid coordinates');
    }
  }
});

test('inspectPoint passes numeric coordinates as serialized data into the renderer', async () => {
  const { context, calls } = harness();
  await invokeDiagnostic(context, 'inspectPoint', { x: 0, y: 25.5 });
  assert.equal(calls.length, 1);
  assert.ok(calls[0].endsWith('({"x":0,"y":25.5})'));
});

test('Unrecognized diagnostics fail closed without invoking provider operations', async () => {
  const { context, calls } = harness();
  context.browserModule.operations = {
    unknownDiagnostic: () => { throw new Error('provider operation must not run'); }
  };
  await assert.rejects(invokeDiagnostic(context, 'unknownDiagnostic'), /Unsupported browser diagnostic operation/);
  assert.equal(calls.length, 0);
});
