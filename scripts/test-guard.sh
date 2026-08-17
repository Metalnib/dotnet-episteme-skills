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
t "--output-indicator-new is not a write (F10)" "$MAINTAINER" "git diff --output-indicator-new=X HEAD" 0 "$REPO_ROOT"
t "git diff --no-index arbitrary read blocked" "$REVIEWER" "git diff --no-index /etc/passwd /dev/null" 2
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

# Refactor and QA lanes carry the same restrictions as the review lanes
CARTOGRAPHER="dotnet-episteme-skills:refactor:cartographer"
QA_ACCEPTANCE="dotnet-episteme-skills:qa:acceptance"
t "refactor lane: git diff allowed" "$CARTOGRAPHER" "git diff HEAD" 0
t "refactor lane: write blocked" "$CARTOGRAPHER" "touch /tmp/x" 2
t "refactor lane: chaining blocked" "dotnet-episteme-skills:refactor:tracer" "git diff && rm -rf /" 2
t "refactor lane: synopsis query allowed" "dotnet-episteme-skills:refactor:conformance-auditor" "synopsis query impact --node X --json" 0
t "refactor lane: rg allowed" "$CARTOGRAPHER" "rg 'return new ' src/" 0
t "refactor lane: rg -o allowed (only-matching, not a write)" "$CARTOGRAPHER" "rg -o 'Adapterw+' src/" 0
t "refactor lane: rg redirect blocked" "$CARTOGRAPHER" "rg pattern > /tmp/out" 2
# F1: tool-own exec/write flags (no shell operator) must be blocked
t "refactor lane: rg --pre RCE blocked" "$CARTOGRAPHER" "rg --pre /tmp/evil.sh pattern src/" 2
t "refactor lane: rg --pre= RCE blocked" "$CARTOGRAPHER" "rg --pre=/tmp/evil.sh pattern ." 2
t "refactor lane: rg --hostname-bin blocked" "$CARTOGRAPHER" "rg --hostname-bin /tmp/evil pattern" 2
t "refactor lane: rg -z decompressor spawn blocked" "$CARTOGRAPHER" "rg -z pattern ." 2
t "refactor lane: rg --search-zip blocked" "$CARTOGRAPHER" "rg --search-zip pattern ." 2
t "refactor lane: tree -o write blocked" "$CARTOGRAPHER" "tree -o /tmp/owned" 2
t "refactor lane: tree -o glued write blocked" "$CARTOGRAPHER" "tree -o/tmp/owned" 2
t "refactor lane: tree read allowed" "$CARTOGRAPHER" "tree src" 0
t "refactor lane: file -C compile blocked" "$CARTOGRAPHER" "file -C -m /tmp/x" 2
t "refactor lane: file read allowed" "$CARTOGRAPHER" "file src/Foo.cs" 0
t "refactor lane: rg --pre-glob blocked" "$CARTOGRAPHER" "rg --pre-glob '*.pdf' pattern" 2
# F2: clustered short flags must not bypass the danger-flag check
t "refactor lane: file -Cm clustered compile blocked" "$CARTOGRAPHER" "file -Cm /tmp/x" 2
t "refactor lane: rg -zi clustered decompress blocked" "$CARTOGRAPHER" "rg -zi pattern ." 2
t "refactor lane: fd -Hx clustered exec blocked" "$CARTOGRAPHER" "fd -Hx rm" 2
t "refactor lane: rg -x line-regexp NOT blocked (safe on rg)" "$CARTOGRAPHER" "rg -x pattern src" 0
t "refactor lane: file -c check-magic NOT blocked (lowercase, safe)" "$CARTOGRAPHER" "file -c src/Foo.cs" 0
# F1: absolute paths and parent escapes confined to the project
t "refactor lane: cat absolute path blocked" "$CARTOGRAPHER" "cat /etc/passwd" 2
t "refactor lane: rg absolute search path blocked" "$CARTOGRAPHER" "rg -n password /Users/hgg" 2
t "refactor lane: find from root blocked" "$CARTOGRAPHER" "find / -name id_rsa" 2
t "refactor lane: home-relative path blocked" "$CARTOGRAPHER" "cat ~/.ssh/id_rsa" 2
t "refactor lane: parent escape blocked" "$CARTOGRAPHER" "cat ../../etc/passwd" 2
t "refactor lane: rg -f absolute pattern-file blocked" "$CARTOGRAPHER" "rg -f /etc/passwd ." 2
t "refactor lane: rg --file= absolute blocked" "$CARTOGRAPHER" "rg --file=/etc/passwd ." 2
t "refactor lane: relative in-project path allowed" "$CARTOGRAPHER" "cat src/Foo.cs" 0
t "refactor lane: quoted pattern containing slash allowed" "$CARTOGRAPHER" "rg 'GET /api' src" 0
t "refactor lane: ls allowed" "dotnet-episteme-skills:refactor:surveyor" "ls -la src" 0
t "refactor lane: wc allowed" "dotnet-episteme-skills:refactor:surveyor" "wc -l src/Foo.cs" 0
t "refactor lane: command -v probe allowed" "$CARTOGRAPHER" "command -v rg" 0
t "refactor lane: which probe allowed" "$CARTOGRAPHER" "which fd" 0
t "refactor lane: bare command execution blocked" "$CARTOGRAPHER" "command rm -rf /tmp/x" 2
t "refactor lane: fd allowed" "$CARTOGRAPHER" "fd -e cs . src" 0
t "refactor lane: fd --exec blocked" "$CARTOGRAPHER" "fd -e cs --exec rm" 2
t "refactor lane: fd -x blocked" "$CARTOGRAPHER" "fd -e cs -x rm" 2
t "refactor lane: grep -x allowed (whole-line match, not exec)" "$CARTOGRAPHER" "grep -x pattern src/Foo.cs" 0
t "refactor lane: find without exec allowed" "$CARTOGRAPHER" "find src -name '*.cs'" 0
t "refactor lane: find -delete blocked" "$CARTOGRAPHER" "find src -name '*.cs' -delete" 2
t "refactor lane: find -exec blocked" "$CARTOGRAPHER" "find src -name '*.cs' -exec rm {} +" 2
t "refactor lane: curl still blocked" "dotnet-episteme-skills:refactor:surveyor" "curl http://example.com" 2
t "review lane: rg still blocked" "$REVIEWER" "rg 'pattern' src/" 2
t "qa lane: git log allowed" "$QA_ACCEPTANCE" "git log --oneline -3" 0
t "qa lane: git merge-base allowed" "$QA_ACCEPTANCE" "git merge-base HEAD origin/main" 0
t "refactor lane: git rev-parse allowed" "$CARTOGRAPHER" "git rev-parse --show-toplevel" 0
t "qa lane: curl blocked" "$QA_ACCEPTANCE" "curl http://example.com" 2
t "qa lane: redirection blocked" "dotnet-episteme-skills:qa:dead-code" "git log > /tmp/out" 2
t "qa lane: synopsis git-scan allowed" "dotnet-episteme-skills:qa:reuse-design" "synopsis git-scan /ws --base main --json" 0
t "refactor lane: git diff --no-index blocked" "$CARTOGRAPHER" "git diff --no-index /etc/passwd /dev/null" 2

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
tc "refactor role: git diff allowed" "refactor-cartographer" "git diff HEAD" 0
tc "refactor role: write blocked" "refactor-tracer" "touch /tmp/x" 2
tc "qa role: git show allowed" "qa-acceptance" "git show HEAD" 0
tc "qa role: chaining blocked" "qa-dead-code" "git diff && curl http://evil" 2
tc "unrelated qa-* role untouched" "qa-tester" "rm -rf /tmp/x" 0
tc "unrelated refactor-* role untouched" "refactor-helper" "rm -rf /tmp/x" 0

echo
if [ "$FAILURES" -gt 0 ]; then
  echo "Guard tests failed: $FAILURES"
  exit 1
fi
echo "Guard tests passed."
