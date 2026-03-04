#!/usr/bin/env bash
# List public types (classes, interfaces, enums) in a NuGet package.
# Usage: list-types.sh <package-name> <version> [tfm]
# Auto-detects whether to use dotnet-inspect or ilspycmd.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ $# -lt 2 ]]; then
    echo "Usage: list-types.sh <package-name> <version> [tfm]" >&2
    echo "Example: list-types.sh PrimeLabs.Service.Assets.Client 1.1.1" >&2
    exit 1
fi

PACKAGE_NAME="$1"
VERSION="$2"
TFM="${3:-}"

TOOL=$("$SCRIPT_DIR/detect-tool.sh")

case "$TOOL" in
    dotnet-inspect)
        if [[ -n "$TFM" ]]; then
            dotnet-inspect type --package "$PACKAGE_NAME" --version "$VERSION" --framework "$TFM"
        else
            dotnet-inspect type --package "$PACKAGE_NAME" --version "$VERSION"
        fi
        ;;
    ilspycmd)
        if [[ -n "$TFM" ]]; then
            DLL=$("$SCRIPT_DIR/find-dll.sh" "$PACKAGE_NAME" "$VERSION" "$TFM")
        else
            DLL=$("$SCRIPT_DIR/find-dll.sh" "$PACKAGE_NAME" "$VERSION")
        fi
        ilspycmd -l ci "$DLL"
        ;;
    *)
        echo "ERROR: Unknown tool: $TOOL" >&2
        exit 1
        ;;
esac
