#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

SKILL_BIN="$SCRIPT_DIR/../../skills/dotnet-techne-synopsis/bin"
RELEASE_DIR="$SCRIPT_DIR/artifacts/release"

RIDS=("osx-arm64" "win-x64" "linux-x64")

for RID in "${RIDS[@]}"; do
    echo "=== Publishing $RID ==="
    dotnet publish Synopsis/Synopsis.csproj -c Release -r "$RID"
    echo ""
done

echo "=== Published to skill bin/ ==="
for RID in "${RIDS[@]}"; do
    SIZE=$(du -sh "$SKILL_BIN/$RID" | cut -f1)
    echo "  $RID: $SIZE"
done

# Create release archives for GitHub Releases upload
echo ""
echo "=== Creating release archives ==="
mkdir -p "$RELEASE_DIR"

for RID in "${RIDS[@]}"; do
    if [[ "$RID" == win-* ]]; then
        # Windows: zip (PowerShell Expand-Archive compatible)
        ARCHIVE="$RELEASE_DIR/synopsis-${RID}.zip"
        rm -f "$ARCHIVE"
        (cd "$SKILL_BIN/$RID" && zip -r -q "$ARCHIVE" .)
        echo "  $ARCHIVE ($(du -sh "$ARCHIVE" | cut -f1))"
    else
        # macOS/Linux: tar.gz
        ARCHIVE="$RELEASE_DIR/synopsis-${RID}.tar.gz"
        rm -f "$ARCHIVE"
        tar -czf "$ARCHIVE" -C "$SKILL_BIN/$RID" .
        echo "  $ARCHIVE ($(du -sh "$ARCHIVE" | cut -f1))"
    fi
done

echo ""
echo "=== Done ==="
echo "Skill binaries: $SKILL_BIN"
echo "Release archives: $RELEASE_DIR"
echo ""
echo "To upload to GitHub Release:"
echo "  gh release create v1.x.0 $RELEASE_DIR/synopsis-*.{tar.gz,zip} --title 'v1.x.0'"
