const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(
  path.resolve(__dirname, "../integrations/Mezhs.Integrations.ChatGpt/browser/chatgpt.ts"),
  "utf8"
);
const start = source.indexOf("async function nativeChatRequirementsHeaders(");
const end = source.indexOf("async function accessToken(", start);
assert.ok(start >= 0 && end > start);

const inspectRequirements = vm.runInNewContext(
  source.slice(start, end) + "\nnativeChatRequirementsHeaders"
);

test("Rspack diagnostic counts partial provider signatures without guessing a replacement", async () => {
  function partialRequirements() {
    const chatReq = true, proofToken = false;
    return { chatReq, proofToken };
  }
  function separateEnforcement() {
    const turnstileToken = null, getEnforcementTokenSync = null;
    return { turnstileToken, getEnforcementTokenSync };
  }

  const loader = function loader() {};
  loader.c = {
    moduleA: { exports: { partialRequirements } },
    moduleB: { exports: { separateEnforcement } }
  };

  const window = {
    webContents: {
      executeJavaScript: async script => {
        assert.doesNotThrow(() => new Function(`return ${script};`));
        const runnable = script.replace(
          "await import(runtimeUrl)",
          "await __testImport(runtimeUrl)"
        );
        const fakeContext = {
          performance: {
            getEntriesByType: () => [{
              name: "https://chatgpt.com/cdn/assets/633146.abc123.js"
            }]
          },
          __testImport: async () => ({ __webpack_require__: loader })
        };
        return vm.runInNewContext(runnable, fakeContext);
      }
    }
  };

  await assert.rejects(
    inspectRequirements(window, { system_hints: [] }),
    error => {
      assert.match(error.message, /provider was not found in the loaded Rspack frontend/);
      const matches = /\(counts=(\{.*\})\)\./.exec(error.message);
      assert.ok(matches, "diagnostic counters are present");
      const counts = JSON.parse(matches[1]);
      assert.equal(counts.bundles, 1);
      assert.equal(counts.loaders, 1);
      assert.equal(counts.cachedModules, 2);
      assert.equal(counts.matchingProviders, 0);
      assert.equal(counts.functionsInspected, 2);
      assert.equal(counts.markerCounts.chatReq, 1);
      assert.equal(counts.markerCounts.proofToken, 1);
      assert.equal(counts.markerCounts.turnstileToken, 1);
      assert.equal(counts.markerCounts.getEnforcementTokenSync, 1);
      assert.equal(counts.markerCounts.getEnforcementToken, 1);
      assert.equal(counts.markerCoverage[2], 1);
      assert.equal(counts.markerCoverage[3], 1);
      assert.doesNotMatch(error.message, /Bearer |Cookie:|secret-token|source code/);
      return true;
    }
  );
});
