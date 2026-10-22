'use strict';

const { execFileSync, execSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '../..');
const generatedDir = '.agents/codegraph/';
const raw = execSync(
  'codegraph graph --root . --json --stdout --no-progress --ignore-glob ".agents/codegraph/**"',
  { cwd: root, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 }
);
const graph = JSON.parse(raw);
if (!graph || Array.isArray(graph) || !Array.isArray(graph.files) ||
    !Array.isArray(graph.fileEdges) || !graph.analysis ||
    Object.keys(graph).sort().join(',') !== 'analysis,fileEdges,files') {
  throw new Error('Unexpected Codegraph graph --json schema');
}

const tracked = new Set(execFileSync('git', ['ls-files', '-z'], {
  cwd: root, encoding: 'utf8'
}).split('\0').filter(Boolean));
const within = (relative) => relative && relative !== '..' &&
  !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
const realRoot = fs.realpathSync.native(root);
const seen = new Set();

graph.files = graph.files.map((absolute) => {
  if (typeof absolute !== 'string' || !path.isAbsolute(absolute)) {
    throw new Error('Codegraph returned a non-absolute file path');
  }
  const relative = path.relative(root, absolute);
  const portable = relative.split(path.sep).join('/');
  if (!within(relative) || !tracked.has(portable) ||
      portable.startsWith(generatedDir) || seen.has(portable) ||
      !within(path.relative(realRoot, fs.realpathSync.native(absolute)))) {
    throw new Error(`Invalid or untracked Codegraph file: ${absolute}`);
  }
  seen.add(portable);
  return portable;
});

const fileIndex = (id) => Number.isInteger(id) && id >= 0 && id < graph.files.length;
for (const edge of graph.fileEdges) {
  if (!edge || !fileIndex(edge.from) || !edge.to ||
      (edge.to.type === 'file'
        ? !fileIndex(edge.to.path)
        : edge.to.type !== 'external' || typeof edge.to.name !== 'string') ||
      typeof edge.raw !== 'string') {
    throw new Error('Invalid Codegraph dependency edge');
  }
}

// Reject unexpected machine-specific paths anywhere, not just in files[].
const machinePath = /(?:^|[^\w/])(?:[A-Za-z]:[\\/]|\\\\[^\\/ \t\r\n]+[\\/]|\/(?:Users|home|mnt|tmp|var|private|Volumes|root)\/)/;
function checkPortable(value) {
  if (typeof value === 'string' && machinePath.test(value)) {
    throw new Error('Machine-specific path found outside file conversion');
  }
  if (Array.isArray(value)) value.forEach(checkPortable);
  else if (value && typeof value === 'object') Object.values(value).forEach(checkPortable);
}
checkPortable(graph);

fs.writeFileSync(path.join(__dirname, 'graph.json'), `${JSON.stringify(graph, null, 2)}\n`);
console.log(`Portable Codegraph: ${graph.files.length} files, ${graph.fileEdges.length} edges`);
