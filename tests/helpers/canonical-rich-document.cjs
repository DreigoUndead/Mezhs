"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const root = path.resolve(__dirname, "../..");
const project = path.join(root, "tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj");
const executable = path.join(root, "tests/Mezhs.RichUi.TestApp/bin/Release/net10.0/Mezhs.RichUi.TestApp.dll");
let prepared = false;
const cache = new Map();

function run(file, args, input) {
  const result = spawnSync(file, args, {
    cwd: root,
    encoding: "utf8",
    input,
    maxBuffer: 16 * 1024 * 1024,
    windowsHide: true,
    timeout: 120000,
  });
  if (result.error || result.status !== 0) {
    throw new Error(
      [`Canonical C# parser command failed: ${file} ${args.join(" ")}`,
        result.error?.message || "", result.stdout || "", result.stderr || ""].join("\n")
    );
  }
  return result.stdout;
}

function ensureParser() {
  if (prepared) return;
  if (!fs.existsSync(executable)) {
    run("dotnet", ["build", project, "-c", "Release", "--nologo", "-v:q"]);
  }
  prepared = true;
}

function canonicalDocuments(inputs) {
  if (!Array.isArray(inputs) || !inputs.every(s => typeof s === "string"))
    throw new Error("Canonical document inputs must be strings");
  ensureParser();
  const missing = [...new Set(inputs.filter(s => !cache.has(s)))];
  if (missing.length > 0) {
    const parsed = JSON.parse(run("dotnet", [executable, "--parse-stdin"], JSON.stringify(missing)));
    if (!Array.isArray(parsed) || parsed.length !== missing.length)
      throw new Error("Canonical C# parser returned an invalid batch");
    parsed.forEach((document, index) => cache.set(missing[index], document));
  }
  return inputs.map(s => cache.get(s));
}

function canonicalDocument(source) {
  return canonicalDocuments([source])[0];
}

module.exports = { canonicalDocument, canonicalDocuments };
