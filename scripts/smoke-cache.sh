#!/usr/bin/env bash
# smoke-cache.sh — Task 5.4: seed the locally-packed dmoncore into an isolated NuGet cache
# and verify the publish closure layout is correct, then confirm dotnet exec emits agentReady.
#
# What it proves:
#   - dmoncore.dll + deps.json + runtimeconfig.json are at the package root (cache layout).
#   - NuGetCoreAcquisitionSource.TryGetCompatibleCachedVersion finds dmoncore.dll via
#     Path.Combine(pkg.ExpandedPath, "dmoncore.dll") — which is the runtime contract.
#   - dotnet exec of dmoncore.dll emits agentReady with protocolVersion Major.Minor == 0.1.
#
# The Tier-4 network acquisition (NuGetCoreAcquisitionSource.AcquireAsync against nuget.org)
# is intentionally NOT exercised here: the feed URL is hardcoded to nuget.org (group-3 surface,
# out of scope). The network path is deferred to the first real nuget.org publish (group 6).
#
# Run from the repo root: bash scripts/smoke-cache.sh
# Requires: dotnet 10 SDK
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FEED="$REPO/.pack-out"
CACHE_DIR="$(mktemp -d)"
STUB_DIR="$(mktemp -d)"
OUT_FILE="$(mktemp)"

cleanup() {
    rm -rf "$CACHE_DIR" "$STUB_DIR" "$OUT_FILE" 2>/dev/null || true
    [ -n "${DMONCORE_PID:-}" ] && kill "$DMONCORE_PID" 2>/dev/null || true
}
trap cleanup EXIT

echo "==> Packing dmoncore to $FEED"
rm -rf "$FEED"
mkdir -p "$FEED"
dotnet pack "$REPO/src/Dmon.Core/Dmon.Core.csproj" -c Release -o "$FEED" --nologo

NUPKG=$(ls "$FEED"/dmoncore.*.nupkg | head -1)
VERSION=$(basename "$NUPKG" | sed 's/dmoncore\.\(.*\)\.nupkg/\1/')
echo "==> Packed: dmoncore $VERSION"

echo "==> Seeding into isolated NuGet cache: $CACHE_DIR"
cat > "$STUB_DIR/stub.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <MinVerSkip>true</MinVerSkip>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="dmoncore" Version="$VERSION" />
  </ItemGroup>
</Project>
CSPROJ

cat > "$STUB_DIR/nuget.config" <<NUGET
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="local" value="$FEED" />
  </packageSources>
</configuration>
NUGET

NUGET_PACKAGES="$CACHE_DIR" dotnet restore "$STUB_DIR/stub.csproj" \
    --no-cache \
    --nologo \
    -v minimal

EXPANDED="$CACHE_DIR/dmoncore/$(echo "$VERSION" | tr '[:upper:]' '[:lower:]')"
echo "==> Checking expanded path: $EXPANDED"

for f in dmoncore.dll dmoncore.deps.json dmoncore.runtimeconfig.json; do
    if [ ! -f "$EXPANDED/$f" ]; then
        echo "FAIL: $f not found at $EXPANDED/$f"
        exit 1
    fi
done
echo "==> dmoncore.dll + deps.json + runtimeconfig.json confirmed at cache root."

echo "==> Launching dmoncore via dotnet exec (waiting for agentReady)"
# Run dmoncore, capture stdout to a temp file, kill after 15s.
dotnet exec "$EXPANDED/dmoncore.dll" > "$OUT_FILE" 2>/dev/null &
DMONCORE_PID=$!

# Poll the output file for agentReady (emitted immediately on startup).
DEADLINE=$((SECONDS + 15))
AGENT_READY=""
while [ "$SECONDS" -lt "$DEADLINE" ]; do
    if grep -q '"type":"agentReady"' "$OUT_FILE" 2>/dev/null; then
        AGENT_READY=$(grep '"type":"agentReady"' "$OUT_FILE" | head -1)
        break
    fi
    sleep 0.2
done

kill "$DMONCORE_PID" 2>/dev/null || true
DMONCORE_PID=""

if [ -z "$AGENT_READY" ]; then
    echo "FAIL: did not read agentReady from dmoncore within 15 s."
    echo "stdout captured:"
    cat "$OUT_FILE"
    exit 1
fi

PROTOCOL=$(echo "$AGENT_READY" | grep -o '"protocolVersion":"[^"]*"' | sed 's/"protocolVersion":"\([^"]*\)"/\1/')
echo "==> agentReady received. protocolVersion=$PROTOCOL"

MAJOR_MINOR=$(echo "$PROTOCOL" | cut -d. -f1,2)
if [ "$MAJOR_MINOR" != "0.1" ]; then
    echo "FAIL: expected protocolVersion Major.Minor=0.1, got $PROTOCOL"
    exit 1
fi

echo ""
echo "PASS: cache layout confirmed; dmoncore launched and emitted agentReady (protocolVersion $PROTOCOL)."
