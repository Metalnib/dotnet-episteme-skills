#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || dirname "$(dirname "$0")")"
PLUGIN_JSON="$REPO_ROOT/.claude-plugin/plugin.json"
ERRORS=0

err() { echo "ERROR: $*" >&2; ERRORS=$((ERRORS + 1)); }
ok()  { echo "OK: $*"; }

if [ ! -f "$PLUGIN_JSON" ]; then
  err "Missing $PLUGIN_JSON"
else
  if ! python3 -m json.tool "$PLUGIN_JSON" > /dev/null 2>&1; then
    err "$PLUGIN_JSON is not valid JSON"
  else
    ok "plugin.json syntax is valid"
  fi
fi

for key in name display_name version license skill_prefix skills; do
  if ! python3 -c "import json,sys; d=json.load(open('$PLUGIN_JSON')); sys.exit(0 if '$key' in d else 1)" 2>/dev/null; then
    err "plugin.json missing required key: $key"
  fi
done

skill_paths="$(python3 -c "import json; d=json.load(open('$PLUGIN_JSON')); [print(s.get('path','')) for s in d.get('skills',[])]" 2>/dev/null || true)"

while IFS= read -r rel_path; do
  [ -z "$rel_path" ] && continue
  full_path="$REPO_ROOT/$rel_path"
  if [ ! -f "$full_path" ]; then
    err "Registered skill path not found: $rel_path"
    continue
  fi

  if ! grep -q '^---$' "$full_path"; then
    err "$rel_path missing YAML frontmatter block"
    continue
  fi

  frontmatter="$(awk '/^---$/{n++; next} n==1{print} n>=2{exit}' "$full_path")"
  if ! echo "$frontmatter" | grep -q '^name:'; then
    err "$rel_path missing frontmatter field: name"
  fi
  if ! echo "$frontmatter" | grep -q '^description:'; then
    err "$rel_path missing frontmatter field: description"
  fi
  ok "$rel_path frontmatter looks valid"
done <<< "$skill_paths"

echo
if [ "$ERRORS" -gt 0 ]; then
  echo "Validation failed with $ERRORS error(s)."
  exit 1
fi

echo "Validation passed."
