#!/usr/bin/env bash
# Installs the OpenCode plugin from this checkout. The symlink keeps `git pull`
# as the update path: the plugin resolves its own location at load time.
#
#   scripts/install-opencode.sh              install (and verify)
#   scripts/install-opencode.sh --verify     verify an existing install only
#   scripts/install-opencode.sh --uninstall  remove the symlink
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$(dirname "$0")")" && pwd)"
PLUGIN_SRC="$REPO_ROOT/opencode/dotnet-episteme.js"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/opencode"
PLUGIN_DIR="$CONFIG_DIR/plugin"
LINK="$PLUGIN_DIR/dotnet-episteme.js"
MODE="install"

case "${1:-}" in
  --verify) MODE="verify" ;;
  --uninstall) MODE="uninstall" ;;
  "") ;;
  *) echo "Unknown argument: $1" >&2; exit 2 ;;
esac

if [ "$MODE" = "uninstall" ]; then
  rm -f "$LINK"
  echo "Removed $LINK"
  echo "Skills, agents, the /dotnet-review command, and the Synopsis MCP server are gone from OpenCode."
  exit 0
fi

if [ "$MODE" = "install" ]; then
  [ -f "$PLUGIN_SRC" ] || { echo "ERROR: $PLUGIN_SRC not found" >&2; exit 1; }
  mkdir -p "$PLUGIN_DIR"
  ln -sf "$PLUGIN_SRC" "$LINK"
  echo "Linked $LINK -> $PLUGIN_SRC"

  # Such a file breaks config resolution for the whole session.
  if [ -d "$CONFIG_DIR/agent" ] || [ -d "$CONFIG_DIR/agents" ]; then
    for dir in "$CONFIG_DIR/agent" "$CONFIG_DIR/agents"; do
      [ -d "$dir" ] || continue
      if grep -rlE '^tools: *[A-Za-z]+,' "$dir" 2>/dev/null | grep -q .; then
        echo "WARNING: $dir contains an agent file with a comma-string 'tools:' key." >&2
        echo "         OpenCode rejects the entire config over it. Delete those files -" >&2
        echo "         this plugin registers the reviewers for you." >&2
      fi
    done
  fi
fi

if [ "$MODE" = "install" ]; then
  # A fresh clone has no bin/, and the MCP startup window is too short to
  # download one.
  DETECT="$REPO_ROOT/skills/dotnet-techne-synopsis/scripts/detect-tool.sh"
  if [ -x "$DETECT" ]; then
    if binary="$("$DETECT" 2>/dev/null)"; then
      echo "Synopsis binary ready: $binary"
    else
      echo "WARNING: no Synopsis binary resolved; the MCP server may fail its first" >&2
      echo "         connect while downloading. Re-run this script or start OpenCode twice." >&2
    fi
  fi
fi

if ! command -v opencode >/dev/null 2>&1; then
  echo "opencode not on PATH — skipping verification."
  echo "After starting OpenCode, check: opencode debug skill | opencode agent list | opencode mcp list"
  exit 0
fi

echo
echo "Verifying with the OpenCode CLI:"
REPO_ROOT="$REPO_ROOT" python3 - <<'PY'
import json
import os
import subprocess
import sys

root = os.environ["REPO_ROOT"]
try:
    raw = subprocess.run(["opencode", "debug", "config"], capture_output=True, text=True, check=True).stdout
    config = json.loads(raw)
except Exception as exc:  # noqa: BLE001 - any failure here is a failed verification
    print(f"  FAIL  could not read `opencode debug config`: {exc}")
    sys.exit(1)

failures = 0


def check(label, ok, detail=""):
    global failures
    print(f"  {'OK  ' if ok else 'FAIL'}  {label}{'' if ok else f' — {detail}'}")
    if not ok:
        failures += 1


paths = (config.get("skills") or {}).get("paths") or []
check("skills path registered", os.path.join(root, "skills") in paths, f"skills.paths = {paths}")

agents = config.get("agent") or {}
expected = {
    "review-correctness",
    "review-performance",
    "review-security-observability",
    "review-data-messaging",
    "review-generalist",
    "review-maintainer",
}
missing = sorted(expected - set(agents))
check("six review subagents registered", not missing, f"missing {missing}")

check("/dotnet-review command registered", "dotnet-review" in (config.get("command") or {}))

if sys.platform == "win32":
    print("  SKIP  Synopsis MCP (native Windows — use WSL2 or the CLI)")
else:
    check("Synopsis MCP server registered", "synopsis" in (config.get("mcp") or {}))

sys.exit(1 if failures else 0)
PY

echo
echo 'Done. Try /dotnet-review in OpenCode, and `opencode mcp list` to confirm Synopsis connects.'
