#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
SRC_DIR="$REPO_ROOT/src/synopsis"

# Detect platform
ARCH="$(uname -m)"
OS="$(uname -s)"

case "$OS-$ARCH" in
    Darwin-arm64) RID="osx-arm64" ;;
    Darwin-x86_64) RID="osx-x64" ;;
    Linux-x86_64) RID="linux-x64" ;;
    Linux-aarch64) RID="linux-arm64" ;;
    *) echo "Unsupported platform: $OS-$ARCH" >&2; exit 1 ;;
esac

# --lean flag: framework-dependent (smaller, needs .NET 10 SDK on target)
SELF_CONTAINED="true"
if [[ "${1:-}" == "--lean" ]]; then
    SELF_CONTAINED="false"
    echo "Building framework-dependent (requires .NET 10 on target)..."
else
    echo "Building self-contained for $RID (no SDK needed on target)..."
fi

cd "$SRC_DIR"
dotnet publish Synopsis/Synopsis.csproj -c Release -r "$RID" \
    --self-contained "$SELF_CONTAINED" \
    -o "artifacts/$RID"

BINARY="$SRC_DIR/artifacts/$RID/synopsis"
if [[ ! -x "$BINARY" ]]; then
    echo "ERROR: Build failed - binary not found at $BINARY" >&2
    exit 1
fi

SIZE=$(du -sh "$BINARY" | cut -f1)
echo ""
echo "Published: $BINARY ($SIZE)"
echo ""
echo "Add to PATH:"
echo "  export PATH=\"\$PATH:$SRC_DIR/artifacts/$RID\""
echo ""
echo "Or symlink:"
echo "  ln -sf \"$BINARY\" /usr/local/bin/synopsis"
