#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

echo "== build"
dotnet build Cameo.slnx -c Release -v quiet

echo "== tests"
dotnet test Cameo.slnx -c Release --no-build --verbosity minimal

echo "== Cameo examples"
bash examples/run.sh

echo "== Cameo.Embed static C++20 VM"
if command -v g++ >/dev/null 2>&1; then
  bash embed/cameo_embed/run.sh
else
  echo "skipped (g++ not installed)"
fi

echo "== vscode extension"
if command -v node >/dev/null 2>&1; then
  CAMEO_BIN="$ROOT/src/Cameo.Cli/bin/Release/net11.0/cameo"
  if [ -x "$CAMEO_BIN" ]; then
    node editors/vscode/test.js "$CAMEO_BIN"
  else
    node editors/vscode/test.js
  fi
else
  echo "skipped (node not installed)"
fi

echo "all checks passed"
