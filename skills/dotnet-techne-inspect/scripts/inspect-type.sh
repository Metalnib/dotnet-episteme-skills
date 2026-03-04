#!/usr/bin/env bash
# Decompile a specific type from a NuGet package to show its full API surface.
# Usage: inspect-type.sh <full-type-name> <package-name> <version> [tfm]
# Auto-detects whether to use dotnet-inspect or ilspycmd.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ $# -lt 3 ]]; then
    echo "Usage: inspect-type.sh <full-type-name> <package-name> <version> [tfm]" >&2
    echo "Example: inspect-type.sh PrimeLabs.Service.Assets.Client.IAssetsApi PrimeLabs.Service.Assets.Client 1.1.1" >&2
    exit 1
fi

TYPE_NAME="$1"
PACKAGE_NAME="$2"
VERSION="$3"
TFM="${4:-}"

TOOL=$("$SCRIPT_DIR/detect-tool.sh")

case "$TOOL" in
    dotnet-inspect)
        if [[ -n "$TFM" ]]; then
            dotnet-inspect member --type "$TYPE_NAME" --package "$PACKAGE_NAME" --version "$VERSION" --framework "$TFM"
        else
            dotnet-inspect member --type "$TYPE_NAME" --package "$PACKAGE_NAME" --version "$VERSION"
        fi
        ;;
    ilspycmd)
        if [[ -n "$TFM" ]]; then
            DLL=$("$SCRIPT_DIR/find-dll.sh" "$PACKAGE_NAME" "$VERSION" "$TFM")
        else
            DLL=$("$SCRIPT_DIR/find-dll.sh" "$PACKAGE_NAME" "$VERSION")
        fi
        ilspycmd -t "$TYPE_NAME" "$DLL"
        ;;
    *)
        echo "ERROR: Unknown tool: $TOOL" >&2
        exit 1
        ;;
esac
