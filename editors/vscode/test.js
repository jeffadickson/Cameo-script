// node editors/vscode/test.js [path to cameo executable]
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { utf16Column } = require('./extension.js');
require('./cameo-diagnostics.test.js');

const cameo = process.argv[2];
if (cameo) {
  const fixture = path.join(__dirname, 'bad.cameo.fixture');
  const run = spawnSync(cameo, ['check', '--json', fixture], { encoding: 'utf8' });
  assert.ifError(run.error);
  assert.strictEqual(run.status, 1, run.stderr);
  const [error] = JSON.parse(run.stdout);
  assert.strictEqual(error.file, fixture);
  assert.strictEqual(error.code, 'CAMEOTYPE');
  assert.strictEqual(error.line, 1);
  const text = fs.readFileSync(fixture, 'utf8').split(/\r?\n/, 1)[0];
  assert.strictEqual(utf16Column(text, error.column), text.indexOf('"wrong"'));
}

// Validate manifest, language configuration, and every regex in the Cameo grammar.
const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, 'package.json'), 'utf8'));
JSON.parse(fs.readFileSync(path.join(__dirname, 'language-configuration.json'), 'utf8'));
assert.ok(manifest.contributes.grammars.some((g) => g.language === 'cameo' && g.scopeName === 'source.cameo'));
let patterns = 0;
function checkGrammar(relPath) {
  const grammar = JSON.parse(fs.readFileSync(path.join(__dirname, relPath), 'utf8'));
  let count = 0;
  (function walk(node) {
    if (Array.isArray(node)) return node.forEach(walk);
    if (node && typeof node === 'object') {
      for (const key of ['match', 'begin', 'end']) {
        if (typeof node[key] === 'string') { new RegExp(node[key].replace(/\\(\d)/g, '(?:$1)')); count++; }
      }
      Object.values(node).forEach(walk);
    }
  })(grammar);
  assert.ok(count > 20, `${relPath} had too few patterns: ${count}`);
  patterns += count;
}
for (const g of manifest.contributes.grammars) checkGrammar(g.path);
console.log(`ok: Cameo diagnostics and ${patterns} grammar patterns compile`);
