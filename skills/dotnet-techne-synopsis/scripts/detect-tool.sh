#!/usr/bin/env bash
set -euo pipefail

REPO="Metalnib/dotnet-episteme-skills"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKILL_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BIN_DIR="$SKILL_DIR/bin"

# Detect platform
ARCH="$(uname -m)"
OS="$(uname -s)"
case "$OS-$ARCH" in
    Darwin-arm64)  RID="osx-arm64" ;;
    Darwin-x86_64) RID="osx-x64" ;;
    Linux-x86_64)  RID="linux-x64" ;;
    Linux-aarch64) RID="linux-arm64" ;;
    *)             RID="" ;;
esac

BINARY="$BIN_DIR/$RID/synopsis"

# 1. Check PATH
if command -v synopsis &>/dev/null; then
    echo "synopsis"
    exit 0
fi

# 2. Check skill bin/
if [[ -n "$RID" && -x "$BINARY" ]]; then
    echo "$BINARY"
    exit 0
fi

# 3. Check dev build artifacts
REPO_ROOT="$(cd "$SKILL_DIR/../.." && pwd)"
for candidate in \
    "$REPO_ROOT/src/synopsis/artifacts/$RID/synopsis" \
    "$REPO_ROOT/src/synopsis/artifacts/osx-arm64/synopsis" \
    "$REPO_ROOT/src/synopsis/artifacts/linux-x64/synopsis"; do
    if [[ -x "$candidate" ]]; then
        echo "$candidate"
        exit 0
    fi
done

# 4. Auto-download from GitHub Releases
if [[ -z "$RID" ]]; then
    echo "ERROR: Unsupported platform $OS-$ARCH" >&2
    exit 1
fi

ASSET="synopsis-${RID}.tar.gz"
URL="https://github.com/${REPO}/releases/latest/download/${ASSET}"

echo "Synopsis not found locally. Downloading for $RID..." >&2

mkdir -p "$BIN_DIR/$RID"

if command -v curl &>/dev/null; then
    HTTP_CODE=$(curl -sL -w "%{http_code}" -o "$BIN_DIR/$RID/$ASSET" "$URL")
    if [[ "$HTTP_CODE" != "200" ]]; then
        rm -f "$BIN_DIR/$RID/$ASSET"
        echo "ERROR: Download failed (HTTP $HTTP_CODE) from $URL" >&2
        echo "No release found. Build from source: cd $REPO_ROOT/src/synopsis && ./publish-all.sh" >&2
        exit 1
    fi
elif command -v wget &>/dev/null; then
    if ! wget -q -O "$BIN_DIR/$RID/$ASSET" "$URL" 2>/dev/null; then
        rm -f "$BIN_DIR/$RID/$ASSET"
        echo "ERROR: Download failed from $URL" >&2
        echo "No release found. Build from source: cd $REPO_ROOT/src/synopsis && ./publish-all.sh" >&2
        exit 1
    fi
else
    echo "ERROR: curl or wget required to download synopsis." >&2
    exit 1
fi

# Extract
tar -xzf "$BIN_DIR/$RID/$ASSET" -C "$BIN_DIR/$RID/"
rm -f "$BIN_DIR/$RID/$ASSET"
chmod +x "$BINARY"

if [[ -x "$BINARY" ]]; then
    echo "Downloaded synopsis to $BINARY" >&2
    echo "$BINARY"
    exit 0
fi

echo "ERROR: Download succeeded but binary not found at $BINARY" >&2
exit 1
