#!/usr/bin/env bash
# pack-core.sh — pack dmoncore and its contract-package deps to a local NuGet feed
# at a stable 0.2.0 version so that #:package dmoncore@0.2.* resolves locally.
#
# Usage:
#   bash scripts/pack-core.sh             # packs to <repo>/.pack-out
#   bash scripts/pack-core.sh /tmp/myfeed # packs to an explicit target directory
#
# Requires: dotnet 10 SDK, git (for MinVer tags)
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Accept an optional target feed directory as the first argument.
FEED="${1:-$REPO/.pack-out}"

echo "==> Cleaning and recreating $FEED"
rm -rf "$FEED"
mkdir -p "$FEED"
# Normalise to an absolute path: it is fed to -p:RestoreSources below, where a
# relative path would resolve against the project directory, not the repo root.
FEED="$(cd "$FEED" && pwd)"

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

echo "==> Packing Dmon.Tools.Builtin (version override: $VERSION_OVERRIDE)"
dotnet pack "$REPO/src/Dmon.Tools.Builtin/Dmon.Tools.Builtin.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE" \
    -p:RestoreSources="$FEED;https://api.nuget.org/v3/index.json"

echo "==> Packing provider packages (version override: $VERSION_OVERRIDE)"
dotnet pack "$REPO/src/Dmon.Providers.Anthropic/Dmon.Providers.Anthropic.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE" \
    -p:RestoreSources="$FEED;https://api.nuget.org/v3/index.json"

dotnet pack "$REPO/src/Dmon.Providers.OpenAI/Dmon.Providers.OpenAI.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE" \
    -p:RestoreSources="$FEED;https://api.nuget.org/v3/index.json"

dotnet pack "$REPO/src/Dmon.Providers.Gemini/Dmon.Providers.Gemini.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE" \
    -p:RestoreSources="$FEED;https://api.nuget.org/v3/index.json"

dotnet pack "$REPO/src/Dmon.Providers.Ollama/Dmon.Providers.Ollama.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE" \
    -p:RestoreSources="$FEED;https://api.nuget.org/v3/index.json"

echo "==> Packing dmoncore library package (version override: $VERSION_OVERRIDE)"
dotnet pack "$REPO/src/Dmon.Core/Dmon.Core.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE"

echo "==> Packing sample extension (version override: $VERSION_OVERRIDE)"
# The sample consumes Dmon.Abstractions as a PackageReference (it mirrors a real
# third-party extension), so its restore needs the local feed. Override the restore
# sources to $FEED — where Dmon.Abstractions 0.2.0 and the contract packages were
# just packed — so packing is independent of any checked-in feed path.
dotnet pack "$REPO/samples/Dmon.SampleExtension/Dmon.SampleExtension.csproj" \
    -c Release -o "$FEED" --nologo \
    -p:MinVerVersionOverride="$VERSION_OVERRIDE" \
    -p:RestoreSources="$FEED;https://api.nuget.org/v3/index.json"

echo ""
echo "PASS: dmoncore $VERSION_OVERRIDE and contract packages packed to $FEED"
echo "      Consumer can reference: #:package dmoncore@0.2.*"
echo "      Builtin tools: #:package Dmon.Tools.Builtin@0.2.*"
echo "      Composed core: #:package dmoncore@0.2.* + #:package Dmon.SampleExtension@0.2.*"
echo "      Providers: Dmon.Providers.Anthropic/OpenAI/Gemini/Ollama@0.2.*"
echo "      Feed: $FEED"
