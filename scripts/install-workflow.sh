#!/usr/bin/env bash
# Installs the dotnet-review dynamic workflow as a native /dotnet-review command
# by copying it into a .claude/workflows/ directory. Optional: the plugin's
# /dotnet-episteme-skills:dotnet-review command runs the bundled script directly
# and always matches the installed plugin version; this copy is for autocomplete
# and standalone use, and must be re-run after plugin updates.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE="$SCRIPT_DIR/../workflows/dotnet-review.js"

case "${1:---user}" in
    --user)    TARGET_DIR="${CLAUDE_CONFIG_DIR:-$HOME/.claude}/workflows" ;;
    --project) TARGET_DIR="$PWD/.claude/workflows" ;;
    *) echo "Usage: install-workflow.sh [--user (default) | --project]" >&2; exit 1 ;;
esac

if [[ ! -f "$SOURCE" ]]; then
    echo "ERROR: $SOURCE not found." >&2
    exit 1
fi

mkdir -p "$TARGET_DIR"
cp "$SOURCE" "$TARGET_DIR/dotnet-review.js"

VERSION="$(grep -m1 '^// version:' "$SOURCE" | sed 's|^// version: *||')"
echo "Installed dotnet-review workflow ${VERSION:-unknown} to $TARGET_DIR/dotnet-review.js"
echo "Run it as /dotnet-review in Claude Code (requires the dotnet-episteme-skills plugin for the reviewer agents)."
echo "Re-run this script after plugin updates to pick up workflow changes."
