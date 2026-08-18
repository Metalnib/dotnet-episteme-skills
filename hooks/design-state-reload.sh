#!/usr/bin/env bash
# SessionStart hook (matchers: startup, compact, clear, resume) for the dotnet-refactor pipeline.
# On a fresh session, and after compaction/clear/resume, re-inject the active DESIGN state file
# so the loop resumes from the artifact instead of from lost conversation history.
set -euo pipefail

dir="${CLAUDE_PROJECT_DIR:-$PWD}/.episteme"

# Newest DESIGN-*.md whose frontmatter is not `status: done`. The status line
# must be read from the YAML frontmatter only - the Decision log body carries
# status/phase entries too, so a whole-file grep would skip an active loop.
# The .episteme/ convention is shared with commands/dotnet-refactor.md.
state_file=""
for f in "$dir"/DESIGN-*.md; do
  [ -e "$f" ] || continue
  # Frontmatter = lines between the line-1 `---` fence and the next `---`.
  frontmatter="$(awk 'NR==1 && $0!="---"{exit} $0=="---"{n++; next} n==1{print} n>=2{exit}' "$f" 2>/dev/null)"
  printf '%s\n' "$frontmatter" | grep -Eq '^status:[[:space:]]*done[[:space:]]*$' && continue
  if [ -z "$state_file" ] || [ "$f" -nt "$state_file" ]; then
    state_file="$f"
  fi
done

[ -z "$state_file" ] && exit 0

# Cap what we inject; the file itself stays the source of truth.
content=$(head -c 16384 "$state_file")

python3 - "$state_file" <<'PY' "$content"
import json, sys
path, content = sys.argv[1], sys.argv[2]
print(json.dumps({
    "hookSpecificOutput": {
        "hookEventName": "SessionStart",
        "additionalContext": (
            f"An active /dotnet-refactor design loop was found at {path}. "
            f"Resume from the phase and status its frontmatter records; the file is the source of truth, not prior conversation.\n\n"
            f"{content}"
        ),
    }
}))
PY
