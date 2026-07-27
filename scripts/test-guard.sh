#!/usr/bin/env bash
# Regression matrix for hooks/git-readonly-guard.sh - the accumulated cases
# from live verification. Run directly or via validate.sh. Exit 1 on any FAIL.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GUARD="$REPO_ROOT/hooks/git-readonly-guard.sh"
FAILURES=0

# t <description> <agent_type or "-"> <command> <expected rc> [project_dir or "-"]
t() {
  local desc="$1" agent="$2" command="$3" expect="$4" proj="${5:--}"
  local input rc=0
  if [ "$agent" = "-" ]; then
    input="{\"tool_input\":{\"command\":$(printf '%s' "$command" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')}}"
  else
    input="{\"agent_type\":\"$agent\",\"tool_input\":{\"command\":$(printf '%s' "$command" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')}}"
  fi
  if [ "$proj" = "-" ]; then
    echo "$input" | env -u CLAUDE_PROJECT_DIR bash "$GUARD" >/dev/null 2>&1 || rc=$?
  else
    echo "$input" | CLAUDE_PROJECT_DIR="$proj" bash "$GUARD" >/dev/null 2>&1 || rc=$?
  fi
  if [ "$rc" = "$expect" ]; then
    echo "PASS: $desc"
  else
    echo "FAIL: $desc (rc=$rc, expected $expect)"
    FAILURES=$((FAILURES + 1))
  fi
}

REVIEWER="dotnet-episteme-skills:review:correctness"
MAINTAINER="dotnet-episteme-skills:review:maintainer"
PLUGIN_BIN="$REPO_ROOT/skills/dotnet-techne-synopsis/bin/linux-x64/synopsis"

# Pass-through: not a review agent
t "main session unrestricted" "-" "rm -rf /tmp/scratch" 0
t "other agent unrestricted" "general-purpose" "npm install" 0

# Read-only git allowed
t "git diff" "$REVIEWER" "git diff HEAD" 0
t "git log -3 (not confused by -o rule)" "$REVIEWER" "git log --oneline -3" 0
t "git blame range" "$MAINTAINER" "git blame -L 40,60 src/Foo.cs" 0
t "git -C project root" "$MAINTAINER" "git -C $REPO_ROOT diff HEAD" 0 "$REPO_ROOT"
t "git -C project subdir" "$MAINTAINER" "git -C $REPO_ROOT/src log --oneline" 0 "$REPO_ROOT"

# Git blocked
t "git push" "$MAINTAINER" "git push origin main" 2 "$REPO_ROOT"
t "git -C outside project" "$MAINTAINER" "git -C /tmp log -p" 2 "$REPO_ROOT"
t "git -C dotdot escape" "$MAINTAINER" "git -C $REPO_ROOT/../other diff" 2 "$REPO_ROOT"
t "git -C without project env" "$MAINTAINER" "git -C $REPO_ROOT diff" 2
t "git -c config injection" "$REVIEWER" "git -c core.pager=evil diff" 2

# Shell operators and writes blocked
t "chaining" "$REVIEWER" "git diff && rm -rf /" 2
t "redirection" "$REVIEWER" "git log > /tmp/out" 2
t "command substitution" "$REVIEWER" "git -C \$(pwd) diff" 2 "$REPO_ROOT"
t "--output" "$REVIEWER" "git log --output=/tmp/x" 2
t "-o flag" "$REVIEWER" "synopsis scan /ws -o graph.json" 2
t "cd" "$REVIEWER" "cd /tmp" 2
t "touch" "$REVIEWER" "touch /tmp/x" 2
t "curl" "$REVIEWER" "curl http://example.com" 2

# Read-only synopsis allowed
t "synopsis query (PATH)" "$REVIEWER" "synopsis query impact --node X --json" 0
t "synopsis git-scan (plugin binary)" "$REVIEWER" "$PLUGIN_BIN git-scan /ws --base main --json" 0

# Synopsis blocked
t "planted repo binary" "$REVIEWER" "/tmp/evil/synopsis query impact --node X" 2
t "synopsis export" "$REVIEWER" "synopsis export json /ws" 2
t "synopsis mcp" "$REVIEWER" "synopsis mcp --root /ws" 2

# Codex-shaped payloads: flat role names, project root from payload cwd
# (no CLAUDE_PROJECT_DIR env), per the rust-v0.145.0 PreToolUse schema.
tc() {
  local desc="$1" agent="$2" command="$3" expect="$4" cwd="${5:-$REPO_ROOT}"
  local input rc=0
  input="{\"hook_event_name\":\"PreToolUse\",\"agent_type\":\"$agent\",\"cwd\":\"$cwd\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":$(printf '%s' "$command" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')}}"
  echo "$input" | env -u CLAUDE_PROJECT_DIR bash "$GUARD" >/dev/null 2>&1 || rc=$?
  if [ "$rc" = "$expect" ]; then
    echo "PASS: (codex) $desc"
  else
    echo "FAIL: (codex) $desc (rc=$rc, expected $expect)"
    FAILURES=$((FAILURES + 1))
  fi
}

tc "git diff allowed" "review-correctness" "git diff HEAD" 0
tc "git push blocked" "review-maintainer" "git push origin main" 2
tc "chaining blocked" "review-generalist" "git diff && rm -rf /" 2
tc "git -C inside payload cwd" "review-maintainer" "git -C $REPO_ROOT/src log" 0
tc "git -C outside payload cwd" "review-maintainer" "git -C /tmp log -p" 2
tc "unrelated review-* role untouched" "review-my-own-agent" "rm -rf /tmp/x" 0
tc "bare lane name untouched" "correctness" "rm -rf /tmp/x" 0

echo
if [ "$FAILURES" -gt 0 ]; then
  echo "Guard tests failed: $FAILURES"
  exit 1
fi
echo "Guard tests passed."
