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

# Prefer the slim framework-dependent build (~7x smaller) when a .NET 10 SDK
# is present; fall back to the self-contained build (also when the release
# predates slim assets).
ASSETS=("synopsis-${RID}.tar.gz")
if command -v dotnet &>/dev/null && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    ASSETS=("synopsis-${RID}-slim.tar.gz" "synopsis-${RID}.tar.gz")
fi

if ! command -v curl &>/dev/null && ! command -v wget &>/dev/null; then
    echo "ERROR: curl or wget required to download synopsis." >&2
    exit 1
fi

mkdir -p "$BIN_DIR/$RID"

download_asset() {
    local asset="$1"
    local url="https://github.com/${REPO}/releases/latest/download/${asset}"
    if command -v curl &>/dev/null; then
        local http_code
        http_code=$(curl -sL -w "%{http_code}" -o "$BIN_DIR/$RID/$asset" "$url")
        [[ "$http_code" == "200" ]] && return 0
    else
        wget -q -O "$BIN_DIR/$RID/$asset" "$url" 2>/dev/null && return 0
    fi
    rm -f "$BIN_DIR/$RID/$asset"
    return 1
}

DOWNLOADED=""
for ASSET in "${ASSETS[@]}"; do
    echo "Synopsis not found locally. Downloading $ASSET..." >&2
    if download_asset "$ASSET"; then
        DOWNLOADED="$ASSET"
        break
    fi
    echo "Download failed for $ASSET; trying next variant." >&2
done

if [[ -z "$DOWNLOADED" ]]; then
    echo "ERROR: Download failed from GitHub Releases ($REPO)." >&2
    echo "No release found. Build from source: cd $REPO_ROOT/src/synopsis && ./publish-all.sh" >&2
    exit 1
fi

# Extract
tar -xzf "$BIN_DIR/$RID/$DOWNLOADED" -C "$BIN_DIR/$RID/"
rm -f "$BIN_DIR/$RID/$DOWNLOADED"
chmod +x "$BINARY"

if [[ -x "$BINARY" ]]; then
    echo "Downloaded synopsis to $BINARY" >&2
    echo "$BINARY"
    exit 0
fi

echo "ERROR: Download succeeded but binary not found at $BINARY" >&2
exit 1
