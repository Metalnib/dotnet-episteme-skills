#!/usr/bin/env bash
# Launches Synopsis as a stdio MCP server for the Claude Code plugin.
# Claude Code starts this in the project directory, so --root is $PWD.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DETECT="$SCRIPT_DIR/../skills/dotnet-techne-synopsis/scripts/detect-tool.sh"

BINARY="$("$DETECT")"

# CLAUDE_PLUGIN_DATA survives plugin updates; fall back for manual runs.
DATA="${CLAUDE_PLUGIN_DATA:-$HOME/.synopsis}"
mkdir -p "$DATA/state"

exec "$BINARY" mcp \
    --root "$PWD" \
    --state-dir "$DATA/state" \
    --log-file "$DATA/synopsis.log"
