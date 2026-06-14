#!/usr/bin/env bash
# pack-core.sh — pack dmoncore and its contract-package deps to the local .pack-out feed
# at a stable 0.2.0 version so that #:package dmoncore@0.2.* resolves locally.
#
# Run from the repo root: bash scripts/pack-core.sh
# Requires: dotnet 10 SDK, git (for MinVer tags)
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FEED="$REPO/.pack-out"

echo "==> Cleaning and recreating $FEED"
rm -rf "$FEED"
mkdir -p "$FEED"

# Stable version override: ensures Major.Minor == 0.2 so:
#   a) the version-skew guard (CheckProtocolVersionSkew) passes, and
#   b) a floating pin dmoncore@0.2.* resolves to this local build even without a git tag.
VERSION_OVERRIDE="0.2.0"

echo "==> Packing contract packages (protocol version override: $VERSION_OVERRIDE)"
dotnet pack "$REPO/src/Dmon.Protocol/Dmon.Protocol.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE"

dotnet pack "$REPO/src/Dmon.Abstractions/Dmon.Abstractions.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE"

dotnet pack "$REPO/src/Dmon.Extensions/Dmon.Extensions.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE"

echo "==> Packing dmoncore library package (version override: $VERSION_OVERRIDE)"
dotnet pack "$REPO/src/Dmon.Core/Dmon.Core.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE"

echo ""
echo "PASS: dmoncore $VERSION_OVERRIDE and contract packages packed to $FEED"
echo "      Consumer can reference: #:package dmoncore@0.2.*"
echo "      Feed: $FEED"
