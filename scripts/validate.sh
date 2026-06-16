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

for key in name; do
  if ! python3 -c "import json,sys; d=json.load(open('$PLUGIN_JSON')); sys.exit(0 if '$key' in d else 1)" 2>/dev/null; then
    err "plugin.json missing required key: $key"
  fi
done

if ! REPO_ROOT="$REPO_ROOT" PLUGIN_JSON="$PLUGIN_JSON" python3 - <<'PY' >/dev/null 2>&1
import json
import os
import pathlib
import sys

plugin_json = pathlib.Path(os.environ["PLUGIN_JSON"])
data = json.loads(plugin_json.read_text())
skills = data.get("skills")
if skills is None:
    sys.exit(0)
if isinstance(skills, str):
    sys.exit(0)
if isinstance(skills, list) and all(isinstance(item, str) for item in skills):
    sys.exit(0)
sys.exit(1)
PY
then
  err "plugin.json field 'skills' must be a string path or an array of string paths"
else
  ok "plugin.json skills field shape is valid"
fi

skill_roots="$(
  REPO_ROOT="$REPO_ROOT" PLUGIN_JSON="$PLUGIN_JSON" python3 - <<'PY' 2>/dev/null || true
import json
import os
import pathlib

repo_root = pathlib.Path(os.environ["REPO_ROOT"])
plugin_json = pathlib.Path(os.environ["PLUGIN_JSON"])
data = json.loads(plugin_json.read_text())

skills = data.get("skills")
roots = []

if skills is None:
    if (repo_root / "skills").is_dir():
        roots.append("./skills")
    if (repo_root / "SKILL.md").is_file():
        roots.append("./SKILL.md")
elif isinstance(skills, str):
    roots.append(skills)
elif isinstance(skills, list):
    roots.extend(skills)

for root in roots:
    print(root)
PY
)"

if [ -z "$skill_roots" ]; then
  echo "WARN: No skills declared and no default skills directory found" >&2
fi

skill_files_list=""
while IFS= read -r rel_root; do
  [ -z "$rel_root" ] && continue
  full_root="$REPO_ROOT/$rel_root"

  if [ -d "$full_root" ]; then
    found_skill_files="$(find "$full_root" -type f -name 'SKILL.md' | sort)"
    if [ -z "$found_skill_files" ]; then
      err "No SKILL.md files found under $rel_root"
      continue
    fi
    skill_files_list="${skill_files_list}"$'\n'"${found_skill_files}"
    continue
  fi

  if [ -f "$full_root" ]; then
    skill_files_list="${skill_files_list}"$'\n'"${full_root}"
    continue
  fi

  err "Registered skill path not found: $rel_root"
done <<< "$skill_roots"

while IFS= read -r skill_file; do
  [ -z "$skill_file" ] && continue
  rel_path="${skill_file#"$REPO_ROOT"/}"

  if ! awk 'NR==1 && $0=="---" { found=1 } END { exit(found?0:1) }' "$skill_file"; then
    err "$rel_path missing opening YAML frontmatter delimiter (---)"
    continue
  fi

  delimiter_count="$(grep -c '^---$' "$skill_file" || true)"
  if [ "${delimiter_count:-0}" -lt 2 ]; then
    err "$rel_path missing closing YAML frontmatter delimiter (---)"
    continue
  fi

  frontmatter="$(awk '/^---$/{n++; next} n==1{print} n>=2{exit}' "$skill_file")"
  if ! printf '%s\n' "$frontmatter" | grep -q '^description:'; then
    err "$rel_path missing frontmatter field: description"
    continue
  fi
  if ! printf '%s\n' "$frontmatter" | grep -q '^name:'; then
    echo "WARN: $rel_path missing frontmatter field: name (allowed, but less explicit)" >&2
  fi
  ok "$rel_path frontmatter looks valid"
done <<< "$skill_files_list"

echo
if [ "$ERRORS" -gt 0 ]; then
  echo "Validation failed with $ERRORS error(s)."
  exit 1
fi

echo "Validation passed."
