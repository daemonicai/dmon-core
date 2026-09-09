#!/usr/bin/env bash
# Publish the demo composition root to a prebuilt core dll, then print the run
# recipe. Re-run after editing Agent.cs or any provider/tool it references.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$REPO/build/demo"

echo "==> Publishing demo/Agent.cs -> $OUT"
dotnet publish "$REPO/demo/Agent.cs" \
    -c Release -o "$OUT" --no-self-contained

echo ""
echo "PASS: prebuilt core at $OUT/Agent.dll"
echo "Run it:"
echo "  export DMON_CORE_PATH=\"$OUT/Agent.dll\""
echo "  cd \"$REPO/demo\" && dotnet run --project ../frontends/Dmon.Terminal"
