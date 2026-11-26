#!/usr/bin/env bash
# Exercise the same CLI and embedded entry points a user invokes.
set -euo pipefail
cd "$(dirname "$0")/.."
output=$(mktemp)
trap 'rm -f "$output"' EXIT
cli=src/Cameo.Cli/bin/Release/net11.0/cameo.dll
for name in 01-file-organizer 02-csv-report 03-json-client 04-async-workflow 06-stock-report 07-recursion-and-captures 08-file-futures 09-explicit-recovery 10-cancellation-deadlines 11-tagged-results 12-recover-deadline 13-using-resource 14-pipelines-and-results 15-interfaces; do
  dotnet "$cli" check "examples/$name.cameo"
  dotnet "$cli" run "examples/$name.cameo" examples/fixtures > "$output"
  diff -u "examples/$name.expected" "$output"
done
dotnet "$cli" fmt --check examples/16-hermetic-engineering.cameo
dotnet "$cli" check examples/16-hermetic-engineering.cameo
dotnet "$cli" run --hermetic examples/16-hermetic-engineering.cameo > "$output"
diff -u examples/16-hermetic-engineering.expected "$output"
dotnet "$cli" fmt --check examples/17-embed-axi-debug.cameo
dotnet "$cli" embed --boot-only-closures examples/17-embed-axi-debug.cameo > /dev/null
dotnet examples/host/bin/Release/net11.0/CameoHost.dll examples/05-embedded-events.cameo > "$output"
diff -u examples/05-embedded-events.expected "$output"
echo 'Cameo examples: 17 passed (CLI, Cameo.Embed static compiler, and managed host)'
