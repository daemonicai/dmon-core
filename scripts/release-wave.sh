#!/usr/bin/env bash
# release-wave.sh — tag every NuGet-family package at <prefix>X.Y.0 for a
# protocol-cycle boundary release (ADR-035 D2 / design D2).
#
# Discovers the tag prefixes from each project's own <MinVerTagPrefix> element
# (the single source of truth — see area-map.yml note in design D6) rather than
# hardcoding a second copy of the list, then emits <prefix>X.Y.0 for each.
#
# Guarded by the same skew check as CheckProtocolVersionSkew in
# Directory.Build.props: X.Y must equal ProtocolVersion.Current, otherwise the
# wave refuses to run.
#
# Usage:
#   bash scripts/release-wave.sh X.Y            # dry run: prints the tag set, pushes nothing
#   bash scripts/release-wave.sh X.Y --push     # creates and pushes the tags to origin
#   PUSH=1 bash scripts/release-wave.sh X.Y     # same, via env var
#
# Example:
#   bash scripts/release-wave.sh 0.2
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() {
    echo "Usage: $(basename "$0") X.Y [--push]" >&2
    echo "  X.Y   protocol Major.Minor to release (must match ProtocolVersion.Current)" >&2
    echo "  --push  actually create and push the tags (default: dry run)" >&2
}

if [ "$#" -lt 1 ]; then
    echo "ERROR: missing required X.Y argument" >&2
    usage
    exit 1
fi

VERSION="$1"
shift || true

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+$ ]]; then
    echo "ERROR: '$VERSION' is not a valid Major.Minor version (expected e.g. 0.2)" >&2
    usage
    exit 1
fi

PUSH="${PUSH:-0}"
for arg in "$@"; do
    case "$arg" in
        --push) PUSH=1 ;;
        *)
            echo "ERROR: unrecognised argument '$arg'" >&2
            usage
            exit 1
            ;;
    esac
done

PROTOCOL_VERSION_FILE="$REPO/core/Dmon.Protocol/ProtocolVersion.cs"
if [ ! -f "$PROTOCOL_VERSION_FILE" ]; then
    echo "ERROR: cannot find $PROTOCOL_VERSION_FILE" >&2
    exit 1
fi

CURRENT="$(grep -oE 'Current\s*=\s*"[0-9]+\.[0-9]+"' "$PROTOCOL_VERSION_FILE" | grep -oE '[0-9]+\.[0-9]+' || true)"
if [ -z "$CURRENT" ]; then
    echo "ERROR: could not parse ProtocolVersion.Current from $PROTOCOL_VERSION_FILE" >&2
    exit 1
fi

if [ "$VERSION" != "$CURRENT" ]; then
    echo "ERROR: requested cycle $VERSION does not match ProtocolVersion.Current ($CURRENT)" >&2
    echo "       refusing to emit or push any tag" >&2
    exit 1
fi

# Discover prefixes from the NuGet-family area dirs only. samples/ and app/
# (dmonium/Desktop) are intentionally excluded — they carry no MinVerTagPrefix.
mapfile -t PREFIXES < <(
    grep -rhoE '<MinVerTagPrefix>[^<]+</MinVerTagPrefix>' \
        "$REPO/core" "$REPO/providers" "$REPO/tools" "$REPO/memory" "$REPO/frontends" \
        --include="*.csproj" \
    | sed -E 's|</?MinVerTagPrefix>||g' \
    | sort
)

EXPECTED_COUNT=18
ACTUAL_COUNT="${#PREFIXES[@]}"
if [ "$ACTUAL_COUNT" -ne "$EXPECTED_COUNT" ]; then
    echo "ERROR: discovered $ACTUAL_COUNT MinVerTagPrefix values, expected exactly $EXPECTED_COUNT" >&2
    echo "       (this catches both prefix drift and accidental samples/app inclusion)" >&2
    printf '  found: %s\n' "${PREFIXES[@]}" >&2
    exit 1
fi

TAGS=()
for prefix in "${PREFIXES[@]}"; do
    TAGS+=("${prefix}${VERSION}.0")
done

echo "==> Cycle-wave release for protocol $VERSION ($ACTUAL_COUNT NuGet-family packages)"
printf '%s\n' "${TAGS[@]}"

if [ "$PUSH" != "1" ]; then
    echo ""
    echo "DRY RUN: no tags created or pushed. Re-run with --push (or PUSH=1) to push."
    exit 0
fi

echo ""
echo "==> Creating tags"
for tag in "${TAGS[@]}"; do
    git -C "$REPO" tag "$tag"
done

echo "==> Pushing tags to origin"
# GitHub triggers NO workflow run when more than 3 tags arrive in a single
# `git push`, so a bulk push of all 18 tags fires zero release runs. Push in
# batches of at most 3 tags per push so every tag's release workflow triggers.
for ((i = 0; i < ${#TAGS[@]}; i += 3)); do
    git -C "$REPO" push origin "${TAGS[@]:i:3}"
done

echo ""
echo "PASS: pushed $ACTUAL_COUNT tags for protocol cycle $VERSION"
