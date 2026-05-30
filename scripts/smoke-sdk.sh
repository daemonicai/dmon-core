#!/usr/bin/env bash
# smoke-sdk.sh — Task 5.3: pack the SDK trio to a local feed and verify the out-of-tree
# consumer (samples/Dmon.ExtensionSmoke) compiles with only package references.
#
# Run from the repo root: bash scripts/smoke-sdk.sh
# Requires: dotnet 10 SDK, git (for MinVer tags)
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FEED="$REPO/.pack-out"
SAMPLE="$REPO/samples/Dmon.ExtensionSmoke"

echo "==> Packing SDK trio to $FEED"
rm -rf "$FEED"
mkdir -p "$FEED"

dotnet pack "$REPO/src/Dmon.Protocol/Dmon.Protocol.csproj"   -c Release -o "$FEED" --nologo
dotnet pack "$REPO/src/Dmon.Abstractions/Dmon.Abstractions.csproj" -c Release -o "$FEED" --nologo
dotnet pack "$REPO/src/Dmon.Extensions/Dmon.Extensions.csproj"  -c Release -o "$FEED" --nologo

# Detect the packed version from the nupkg filename
VERSION=$(ls "$FEED"/Dmon.Protocol.*.nupkg | head -1 | sed 's/.*Dmon\.Protocol\.\(.*\)\.nupkg/\1/')
echo "==> Detected packed version: $VERSION"

echo "==> Building out-of-tree sample against local feed"
dotnet build "$SAMPLE/Dmon.ExtensionSmoke.csproj" \
    -p:DmonSdkVersion="$VERSION" \
    --no-incremental \
    --nologo \
    -v minimal

echo ""
echo "PASS: out-of-tree consumer compiled against Dmon.Extensions $VERSION (package reference only)."
