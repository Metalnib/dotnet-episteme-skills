#!/usr/bin/env bash
# Installs the review dynamic workflow (registers as /dotnet-review-workers) by
# copying it into a .claude/workflows/ directory. Optional: the plugin's
# /dotnet-episteme-skills:dotnet-review command runs the bundled script directly
# and always matches the installed plugin version; this copy is for standalone
# use, and must be re-run after plugin updates.
#
# Deliberately review-only: dotnet-qa.js and dotnet-refactor.js are not
# installable standalone - they depend on their plugin command resolving the
# spec pack / design state first (a bare invocation would silently skip the
# spec cascade or the approval gates).
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
echo "Installed the review workflow ${VERSION:-unknown} to $TARGET_DIR/dotnet-review.js"
echo "Run it as /dotnet-review-workers in Claude Code (requires the dotnet-episteme-skills plugin for the reviewer agents; the plugin's /dotnet-review command is the friendlier entry point)."
echo "Re-run this script after plugin updates to pick up workflow changes."
