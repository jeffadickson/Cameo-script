# Cameo for Visual Studio Code

Syntax highlighting (`source.cameo`) and save-time static diagnostics for `.cameo` files powered by the `cameo` CLI (`cameo check --json`).

## Settings

- `cameo.path`: Path to the `cameo` executable (default: `"cameo"`).
- `cameo.checkOnSave`: Run `cameo check --json` when opening or saving `.cameo` files in trusted workspaces (default: `true`).

## Verification

```bash
node editors/vscode/test.js path/to/cameo
```
