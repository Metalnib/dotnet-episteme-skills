#!/usr/bin/env bash
# PreToolUse guard: this plugin's worker agents (review, refactor and qa lanes)
# may only run read-only git and synopsis commands. Other agents and the main
# session pass through untouched.
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
import json, os, re, shlex, sys

data = json.loads(sys.argv[1])
agent = data.get("agent_type") or ""
# Exact lane sets (Claude namespaced, Codex flat) - a user's unrelated
# "review-*"/"qa-*" role must not be restricted.
GROUPS = {
    "review": {"correctness", "performance", "security-observability",
               "data-messaging", "generalist", "maintainer"},
    "refactor": {"cartographer", "tracer", "conformance-auditor", "surveyor"},
    "qa": {"acceptance", "reuse-design", "dead-code"},
}
matched_group = next((group for group, lanes in GROUPS.items()
                      for p in (f"dotnet-episteme-skills:{group}:", f"{group}-")
                      if agent.startswith(p) and agent[len(p):] in lanes), None)
if matched_group is None:
    # Not one of this plugin's worker agents (main session, other agents,
    # skills): exit 0 = no opinion, the normal permission flow applies.
    sys.exit(0)

cmd = (data.get("tool_input") or {}).get("command") or ""
# No chaining/redirection/substitution - read-only commands need none of it.
if re.search(r"[;&|<>`$]", cmd) or "\n" in cmd:
    print("Blocked for plugin worker agents: shell operators are not allowed.", file=sys.stderr)
    sys.exit(2)
# Refactor workers sweep whole solutions; they get the read-only search/list
# tools on top of git and synopsis - fast tools (rg, fd) plus the GNU
# fallbacks, and `command -v`/`which` to probe which is installed.
#
# The shell-operator block above stops chaining/redirection; but several of
# these tools can execute or write through their OWN flags with no operator at
# all (rg --pre runs a command per file, tree -o writes a file, file -C
# compiles one). Each tool's write/exec flags are denied explicitly. Short
# flags cluster (-Cm is -C -m, -zi is -z -i), so the short-flag half of each
# pattern matches the letter anywhere in a leading cluster; the letter sets are
# per-tool so a safe flag on one tool (rg -x = --line-regexp) is not blocked
# because it is dangerous on another (fd -x = --exec). `-o` is per-tool too:
# rg/grep use it for --only-matching (safe), tree uses it to write (denied).
DANGER_FLAGS = {
    "rg":   r"(^|\s)(--pre|--pre-glob|--hostname-bin|--search-zip)(\s|=|$)|(^|\s)-[A-Za-z]*z",
    "tree": r"(^|\s)(-o|--output)",
    "file": r"(^|\s)(--compile)(\s|=|$)|(^|\s)-[A-Za-z]*C",
    "find": r"(^|\s)(-delete|-exec\w*|-ok\w*|-fprint\w*|-fls)(\s|$)",
    "fd":   r"(^|\s)(--exec|--exec-batch)(\s|=|$)|(^|\s)-[A-Za-z]*[xX]",
}
if matched_group == "refactor":
    if re.match(r"^\s*(command\s+-v|which)\s", cmd):
        sys.exit(0)
    tm = re.match(r"^\s*(rg|fd|grep|ls|eza|cat|head|tail|wc|tree|file|stat|find)\b", cmd)
    if tm:
        danger = DANGER_FLAGS.get(tm.group(1))
        if danger and re.search(danger, cmd):
            print(f"Blocked for plugin worker agents: {tm.group(1)} write/exec flags are not allowed.", file=sys.stderr)
            sys.exit(2)
        # Confine reads to the project: no absolute paths and no parent-directory
        # escapes in the operands. NOTE this governs the SHELL only - the native
        # Read/Grep/Glob tools are not seen by this hook (see
        # docs/reviewer-restrictions.md), so on Claude Code this is defence in
        # depth, not an absolute read sandbox.
        try:
            tokens = shlex.split(cmd)
        except ValueError:
            print("Blocked for plugin worker agents: could not parse the command safely.", file=sys.stderr)
            sys.exit(2)
        for tok in tokens[1:]:
            if tok.startswith("-"):
                if re.search(r"=(/|~)", tok):
                    print("Blocked for plugin worker agents: absolute paths in flag values are not allowed; search within the project.", file=sys.stderr)
                    sys.exit(2)
                continue
            if tok.startswith("/") or tok.startswith("~"):
                print("Blocked for plugin worker agents: absolute paths are not allowed; search within the project (relative paths) or use the Grep tool.", file=sys.stderr)
                sys.exit(2)
            if tok == ".." or tok.startswith("../") or "/../" in tok or tok.endswith("/.."):
                print("Blocked for plugin worker agents: parent-directory escapes are not allowed.", file=sys.stderr)
                sys.exit(2)
        sys.exit(0)
# --output as a whole flag only: git's read-only --output-indicator-{new,old,context}
# write nothing and must not be blocked.
if re.search(r"(^|\s)--output(\s|=|$)", cmd) or re.search(r"(^|\s)-o(\s|=)", cmd):
    print("Blocked for plugin worker agents: -o and --output are not allowed.", file=sys.stderr)
    sys.exit(2)
# Optional `-C <path>` lets reviewers target the repo when their cwd differs,
# but only inside the project directory - otherwise a steered agent could read
# any repo's history on the machine.
m = re.match(r"^\s*git\s+(?:-C\s+([^\s;&|<>`$]+)\s+)?(diff|log|show|blame|status|rev-parse|merge-base)\b", cmd)
if m:
    # --no-index turns git diff into a generic file reader (any path on the
    # machine) - the search tools' path confinement must not be sidestepped.
    if re.search(r"(^|\s)--no-index(\s|=|$)", cmd):
        print("Blocked for plugin worker agents: git diff --no-index reads files outside the repository.", file=sys.stderr)
        sys.exit(2)
    c_path = m.group(1)
    if c_path is None:
        sys.exit(0)
    # Codex has no CLAUDE_PROJECT_DIR; its payload carries the turn cwd instead.
    root = os.environ.get("CLAUDE_PROJECT_DIR") or data.get("cwd")
    if root:
        real, root_real = os.path.realpath(c_path), os.path.realpath(root)
        if real == root_real or real.startswith(root_real + os.sep):
            sys.exit(0)
    print("Blocked for plugin worker agents: git -C is only allowed for paths inside the project directory.", file=sys.stderr)
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

print("Blocked for plugin worker agents: only read-only git (diff, log, show, blame, status, rev-parse, merge-base) and synopsis (query, git-scan, scan, diff, breaking-diff) commands are allowed.", file=sys.stderr)
sys.exit(2)
PY
