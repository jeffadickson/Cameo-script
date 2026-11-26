// Cameo syntax and save-time diagnostics via the `cameo` CLI.
const path = require('path');
const { execFile } = require('child_process');

/** Maps a one-based UTF-8 byte column to VS Code's zero-based UTF-16 position. */
function utf16Column(line, column) {
  let remaining = column - 1;
  let offset = 0;
  for (const character of line) {
    const width = Buffer.byteLength(character, 'utf8');
    // A byte inside a character points to that character, never half a surrogate pair.
    if (remaining < width) break;
    remaining -= width;
    offset += character.length;
  }
  return offset;
}

/** Run the Cameo checker on saved file contents, publishing only the current request. */
function registerCameoDiagnostics(context, vscode, run = execFile) {
  const diagnostics = vscode.languages.createDiagnosticCollection('cameo');
  const pending = new Map();
  const isCameoFile = (document) => document.languageId === 'cameo' && document.uri.scheme === 'file';
  const invalidate = (document) => {
    if (!isCameoFile(document)) return;
    pending.delete(document.uri.toString());
    diagnostics.delete(document.uri);
  };
  const check = (document) => {
    if (!isCameoFile(document)) return;
    invalidate(document);
    const config = vscode.workspace.getConfiguration('cameo');
    if (!vscode.workspace.isTrusted || !config.get('checkOnSave') || document.isDirty) return;
    const file = document.uri.fsPath;
    const directory = path.dirname(file);
    const executable = config.get('path') || 'cameo';
    const key = document.uri.toString();
    const request = { version: document.version };
    pending.set(key, request);

    run(executable, ['check', '--json', file], { cwd: directory, timeout: 30000, windowsHide: true }, (error, stdout) => {
      if (pending.get(key) !== request) return;
      pending.delete(key);
      if (!isCameoFile(document) || document.version !== request.version || document.isDirty) return;
      if (error?.code === 'ENOENT') {
        vscode.window.showWarningMessage(`cameo was not found (setting "cameo.path" is "${executable}").`);
        return;
      }
      // Exit 1 can be an ordinary check failure. The JSON response carries the diagnostics.
      let response;
      try { response = JSON.parse(String(stdout)); }
      catch (err) {
        if (err instanceof SyntaxError) return;
        throw err;
      }
      if (!Array.isArray(response)) return;
      const items = [];
      for (const entry of response) {
        if (!entry || typeof entry.file !== 'string' || path.resolve(directory, entry.file) !== path.resolve(file)
          || !Number.isSafeInteger(entry.line) || entry.line < 1 || entry.line > document.lineCount
          || !Number.isSafeInteger(entry.column) || entry.column < 1 || typeof entry.message !== 'string') continue;
        const line = entry.line - 1;
        const text = document.lineAt(line).text;
        const offset = utf16Column(text, entry.column);
        const word = /^[A-Za-z_0-9]+/.exec(text.slice(offset));
        const end = word ? offset + word[0].length : text.length;
        const item = new vscode.Diagnostic(
          new vscode.Range(new vscode.Position(line, offset), new vscode.Position(line, end)),
          entry.message, entry.warning === true ? vscode.DiagnosticSeverity.Warning : vscode.DiagnosticSeverity.Error);
        item.source = 'cameo';
        if (typeof entry.code === 'string') item.code = entry.code;
        items.push(item);
      }
      diagnostics.set(document.uri, items);
    });
  };

  context.subscriptions.push(diagnostics, { dispose: () => pending.clear() });
  context.subscriptions.push(vscode.workspace.onDidOpenTextDocument(check),
    vscode.workspace.onDidSaveTextDocument(check), vscode.workspace.onDidCloseTextDocument(invalidate),
    vscode.workspace.onDidChangeTextDocument(({ document, contentChanges }) => {
      if (contentChanges.length > 0) invalidate(document);
    }), vscode.workspace.onDidGrantWorkspaceTrust(() => vscode.workspace.textDocuments.forEach(check)));
  vscode.workspace.textDocuments.forEach(check);
}

function activate(context) {
  const vscode = require('vscode');
  registerCameoDiagnostics(context, vscode);
}

function deactivate() {}

module.exports = { activate, deactivate, registerCameoDiagnostics, utf16Column };
