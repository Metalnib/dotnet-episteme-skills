#!/usr/bin/env bash
# PreToolUse guard: the review:* agents may only run read-only git and
# synopsis commands. Other agents and the main session pass through untouched.
set -euo pipefail

INPUT="$(cat)"

# Subagent calls carry agent_type on both Claude Code and Codex (>=0.145);
# main-session calls don't - skip those without spawning python3.
case "$INPUT" in
  *'"agent_type"'*) ;;
  *) exit 0 ;;
esac

PLUGIN_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
python3 - "$INPUT" "$PLUGIN_ROOT" <<'PY'
import json, os, re, sys

data = json.loads(sys.argv[1])
agent = data.get("agent_type") or ""
# Exact lane set (Claude namespaced, Codex flat) - a user's unrelated
# "review-*" role must not be restricted.
LANES = {"correctness", "performance", "security-observability",
         "data-messaging", "generalist", "maintainer"}
if not any(agent.startswith(p) and agent[len(p):] in LANES
           for p in ("dotnet-episteme-skills:review:", "review-")):
    # Not one of this plugin's review agents (main session, other agents,
    # skills): exit 0 = no opinion, the normal permission flow applies.
    sys.exit(0)

cmd = (data.get("tool_input") or {}).get("command") or ""
# No chaining/redirection/substitution - read-only commands need none of it.
if re.search(r"[;&|<>`$]", cmd) or "\n" in cmd or "--output" in cmd or re.search(r"(^|\s)-o(\s|=)", cmd):
    print("Blocked for review agents: shell operators, -o, and --output are not allowed.", file=sys.stderr)
    sys.exit(2)
# Optional `-C <path>` lets reviewers target the repo when their cwd differs,
# but only inside the project directory - otherwise a steered agent could read
# any repo's history on the machine.
m = re.match(r"^\s*git\s+(?:-C\s+([^\s;&|<>`$]+)\s+)?(diff|log|show|blame|status)\b", cmd)
if m:
    c_path = m.group(1)
    if c_path is None:
        sys.exit(0)
    # Codex has no CLAUDE_PROJECT_DIR; its payload carries the turn cwd instead.
    root = os.environ.get("CLAUDE_PROJECT_DIR") or data.get("cwd")
    if root:
        real, root_real = os.path.realpath(c_path), os.path.realpath(root)
        if real == root_real or real.startswith(root_real + os.sep):
            sys.exit(0)
    print("Blocked for review agents: git -C is only allowed for paths inside the project directory.", file=sys.stderr)
    sys.exit(2)

# Read-only synopsis CLI: bare `synopsis` (PATH install) or the plugin-shipped
# binary only - a binary planted inside a reviewed repo must not qualify.
sm = re.match(r"^\s*(\S+)\s+(query|git-scan|scan|diff|breaking-diff|version|--version)\b", cmd)
if sm:
    exe = sm.group(1)
    plugin_root = os.path.realpath(sys.argv[2]) if len(sys.argv) > 2 else None
    exe_ok = exe == "synopsis" or (
        plugin_root is not None
        and os.path.basename(exe) == "synopsis"
        and os.path.realpath(exe).startswith(plugin_root + os.sep))
    if exe_ok:
        sys.exit(0)

print("Blocked for review agents: only read-only git (diff, log, show, blame, status) and synopsis (query, git-scan, scan, diff, breaking-diff) commands are allowed.", file=sys.stderr)
sys.exit(2)
PY
