#!/usr/bin/env bash
# Publish the sandbox-code MLX coding agent to a prebuilt core dll, then print
# the run recipe. Re-run after editing Agent.cs or any referenced provider/tool.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$REPO/build/sandbox-code"

echo "==> Publishing sandbox-code/Agent.cs -> $OUT"
dotnet publish "$REPO/sandbox-code/Agent.cs" \
    -c Release -o "$OUT" --no-self-contained

echo ""
echo "PASS: prebuilt core at $OUT/Agent.dll"
echo "Run it:"
echo "  export DMON_CORE_PATH=\"$OUT/Agent.dll\""
echo "  cd \"$REPO/sandbox-code\" && dmon"
