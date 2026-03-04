#!/usr/bin/env bash
# Resolve the DLL path for a NuGet package from the local cache.
# Usage: find-dll.sh <package-name> <version> [tfm]
# If tfm is omitted, picks the highest available TFM.
# Output: absolute path to the DLL file.

set -euo pipefail

if [[ $# -lt 2 ]]; then
    echo "Usage: find-dll.sh <package-name> <version> [tfm]" >&2
    echo "Example: find-dll.sh Refit 8.0.0 net8.0" >&2
    exit 1
fi

PACKAGE_NAME="$1"
VERSION="$2"
TFM="${3:-}"

# NuGet cache stores package names in lowercase
PACKAGE_LOWER="$(echo "$PACKAGE_NAME" | tr '[:upper:]' '[:lower:]')"
NUGET_CACHE="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
PACKAGE_DIR="$NUGET_CACHE/$PACKAGE_LOWER/$VERSION"

if [[ ! -d "$PACKAGE_DIR" ]]; then
    echo "ERROR: Package not found in NuGet cache: $PACKAGE_DIR" >&2
    echo "Run 'dotnet restore' in a project that references $PACKAGE_NAME $VERSION" >&2
    exit 1
fi

LIB_DIR="$PACKAGE_DIR/lib"
if [[ ! -d "$LIB_DIR" ]]; then
    echo "ERROR: No lib/ directory found in $PACKAGE_DIR" >&2
    exit 1
fi

# Resolve TFM
if [[ -z "$TFM" ]]; then
    # Pick the highest TFM by sorting available directories
    # Prefer net8.0 > net7.0 > net6.0 > netstandard2.1 > netstandard2.0 etc.
    TFM=$(ls -1 "$LIB_DIR" | sort -rV | head -1)
    if [[ -z "$TFM" ]]; then
        echo "ERROR: No TFM directories found in $LIB_DIR" >&2
        exit 1
    fi
fi

TFM_DIR="$LIB_DIR/$TFM"
if [[ ! -d "$TFM_DIR" ]]; then
    echo "ERROR: TFM '$TFM' not found. Available:" >&2
    ls -1 "$LIB_DIR" >&2
    exit 1
fi

# Find the primary DLL (match package name, case-insensitive)
DLL=$(find "$TFM_DIR" -maxdepth 1 -iname "*.dll" | head -1)
if [[ -z "$DLL" ]]; then
    echo "ERROR: No DLL found in $TFM_DIR" >&2
    exit 1
fi

echo "$DLL"
