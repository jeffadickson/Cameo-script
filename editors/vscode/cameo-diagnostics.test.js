const assert = require('node:assert/strict');
const path = require('node:path');
const { registerCameoDiagnostics, utf16Column } = require('./extension.js');

const lineText = 'é😀 invalid';
assert.equal(typeof utf16Column, 'function', 'Cameo diagnostic columns need a UTF-8 to UTF-16 converter');
assert.deepEqual([1, 2, 3, 6, 7, 8, 100].map((column) => utf16Column(lineText, column)),
  [0, 0, 1, 1, 3, 4, lineText.length]);

const file = path.resolve('example with spaces.cameo');
const document = {
  fileName: file, uri: { scheme: 'file', fsPath: file, toString: () => file }, languageId: 'cameo',
  lineCount: 1, lineAt: () => ({ text: lineText }), version: 1
};
const events = {};
const requests = [];
const items = new Map();
function on(name) {
  return (handler) => { events[name] = handler; return { dispose() {} }; };
}
const vscode = {
  workspace: {
    isTrusted: true,
    getConfiguration: () => ({ get: (name) => name === 'path' ? '/tools/cameo' : true }),
    onDidOpenTextDocument: on('open'), onDidSaveTextDocument: on('save'),
    onDidCloseTextDocument: on('close'), onDidChangeTextDocument: on('change'),
    onDidGrantWorkspaceTrust: on('trust'),
    textDocuments: [document]
  },
  languages: { createDiagnosticCollection: (name) => {
    assert.equal(name, 'cameo');
    return { set: (uri, diagnostics) => items.set(uri, diagnostics), delete: (uri) => items.delete(uri) };
  } },
  window: { showWarningMessage() {} },
  Position: class { constructor(line, character) { Object.assign(this, { line, character }); } },
  Range: class { constructor(start, end) { Object.assign(this, { start, end }); } },
  Diagnostic: class { constructor(range, message, severity) { Object.assign(this, { range, message, severity }); } },
  DiagnosticSeverity: { Error: 0, Warning: 1 }
};
const context = { subscriptions: [] };
registerCameoDiagnostics(context, vscode, (...args) => { requests.push(args); });
assert.equal(requests.length, 1, 'open Cameo file checked on activation');
assert.equal(requests[0][0], '/tools/cameo');
assert.deepEqual(requests[0][1], ['check', '--json', file]);
assert.equal(requests[0][2].cwd, path.dirname(file));
events.open({ ...document, languageId: 'teal' });
events.open({ ...document, uri: { ...document.uri, scheme: 'untitled' } });
assert.equal(requests.length, 1, 'Cameo checker ignores Teal and unsaved documents');

function finish(index, message, warning = false) {
  requests[index][3](warning ? null : { code: 1 }, JSON.stringify([
    { file, line: 1, column: 8, message, warning, code: 'CAMEOTYPE' },
    { file: path.resolve('other.cameo'), line: 1, column: 1, message: 'other' },
    { file, line: 2, column: 1, message: 'line outside the document' }
  ]), '');
}
events.save(document);
assert.equal(requests.length, 2);
finish(1, 'latest', true);
assert.equal(items.get(document.uri).length, 1);
const latest = items.get(document.uri)[0];
assert.equal(latest.message, 'latest');
assert.equal(latest.range.start.character, 4);
assert.equal(latest.range.end.character, 11);
assert.equal(latest.severity, vscode.DiagnosticSeverity.Warning);
assert.equal(latest.source, 'cameo');
assert.equal(latest.code, 'CAMEOTYPE');
finish(0, 'stale');
assert.equal(items.get(document.uri)[0], latest);

events.save(document);
document.version++;
finish(2, 'older document');
assert.equal(items.has(document.uri), false);
events.change({ document, contentChanges: [{ text: 'change' }] });
assert.equal(items.has(document.uri), false);

events.save(document);
events.close(document);
finish(3, 'closed document');
assert.equal(items.has(document.uri), false);

events.open(document);
requests[4][3]({ code: 'ENOENT' }, '', '');
assert.equal(items.has(document.uri), false);
events.save(document);
requests[5][3]({ code: 1 }, '[', '');
assert.equal(items.has(document.uri), false);
events.save(document);
requests[6][3](null, '[]', '');
assert.deepEqual(items.get(document.uri), []);

vscode.workspace.isTrusted = false;
events.save(document);
assert.equal(requests.length, 7, 'restricted workspaces do not run the Cameo CLI');
vscode.workspace.isTrusted = true;
events.trust();
assert.equal(requests.length, 8);
document.isDirty = true;
events.change({ document, contentChanges: [{ text: 'unsaved edit' }] });
finish(7, 'saved revision only');
assert.equal(items.has(document.uri), false);

console.log('ok: Cameo diagnostic CLI arguments, severity, UTF-16 columns and invalidation');

// Optional production CLI check. The fixture is read-only and needs no scratch files.
if (require.main === module && process.argv[2]) {
  const { spawnSync } = require('node:child_process');
  const fixture = path.join(__dirname, 'bad.cameo.fixture');
  const run = spawnSync(process.argv[2], ['check', '--json', fixture], { encoding: 'utf8' });
  assert.ifError(run.error);
  assert.equal(run.status, 1, run.stderr);
  const [error] = JSON.parse(run.stdout);
  assert.equal(error.file, fixture);
  assert.equal(error.code, 'CAMEOTYPE');
  assert.equal(error.line, 1);
  const text = require('node:fs').readFileSync(fixture, 'utf8').split(/\r?\n/, 1)[0];
  assert.equal(utf16Column(text, error.column), text.indexOf('"wrong"'));
  console.log('ok: real Cameo JSON checker columns map to the diagnostic token');
}
