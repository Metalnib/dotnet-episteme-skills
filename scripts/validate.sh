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

if ! python3 -c "import json,sys; d=json.load(open('$PLUGIN_JSON')); sys.exit(0 if 'name' in d else 1)" 2>/dev/null; then
  err "plugin.json missing required key: name"
fi

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

# --- Agents (plugin subagents) ---
AGENTS_DIR="$REPO_ROOT/agents"
if [ -d "$AGENTS_DIR" ]; then
  # A present "agents" key replaces the default ./agents scan; an empty array hides everything.
  if ! python3 -c "import json,sys; sys.exit(1 if json.load(open('$PLUGIN_JSON')).get('agents') == [] else 0)" 2>/dev/null; then
    err "plugin.json 'agents' is an empty array — it overrides the default ./agents scan and hides all agents; remove the key"
  fi

  agent_files="$(find "$AGENTS_DIR" -type f -name '*.md' | sort)"
  if [ -z "$agent_files" ]; then
    err "agents/ directory exists but contains no .md agent files"
  fi
  while IFS= read -r agent_file; do
    [ -z "$agent_file" ] && continue
    rel_path="${agent_file#"$REPO_ROOT"/}"
    frontmatter="$(awk '/^---$/{n++; next} n==1{print} n>=2{exit}' "$agent_file")"
    for field in name description; do
      if ! printf '%s\n' "$frontmatter" | grep -q "^${field}:"; then
        err "$rel_path missing frontmatter field: $field"
      fi
    done
    for field in hooks mcpServers permissionMode; do
      if printf '%s\n' "$frontmatter" | grep -q "^${field}:"; then
        err "$rel_path uses '$field' — ignored in plugin agents; move the agent to .claude/agents/ if it needs this"
      fi
    done
    ok "$rel_path agent frontmatter looks valid"
  done <<< "$agent_files"
fi

# --- Commands ---
COMMANDS_DIR="$REPO_ROOT/commands"
if [ -d "$COMMANDS_DIR" ]; then
  while IFS= read -r command_file; do
    [ -z "$command_file" ] && continue
    rel_path="${command_file#"$REPO_ROOT"/}"
    frontmatter="$(awk '/^---$/{n++; next} n==1{print} n>=2{exit}' "$command_file")"
    if ! printf '%s\n' "$frontmatter" | grep -q '^description:'; then
      err "$rel_path missing frontmatter field: description"
    else
      ok "$rel_path command frontmatter looks valid"
    fi
  done <<< "$(find "$COMMANDS_DIR" -type f -name '*.md' | sort)"
fi

# --- MCP config ---
MCP_JSON="$REPO_ROOT/.mcp.json"
if [ -f "$MCP_JSON" ]; then
  if ! python3 -c "import json,sys; sys.exit(0 if isinstance(json.load(open('$MCP_JSON')).get('mcpServers'), dict) else 1)" 2>/dev/null; then
    err ".mcp.json must be valid JSON with an 'mcpServers' object"
  else
    ok ".mcp.json is valid"
  fi
  if [ ! -x "$REPO_ROOT/bin/synopsis-mcp-launcher.sh" ]; then
    err "bin/synopsis-mcp-launcher.sh is missing or not executable"
  else
    ok "bin/synopsis-mcp-launcher.sh is executable"
  fi
fi

# --- Hooks ---
HOOKS_JSON="$REPO_ROOT/hooks/hooks.json"
if [ -f "$HOOKS_JSON" ]; then
  if ! python3 -c "import json,sys; sys.exit(0 if isinstance(json.load(open('$HOOKS_JSON')).get('hooks'), dict) else 1)" 2>/dev/null; then
    err "hooks/hooks.json must be valid JSON with a 'hooks' object"
  else
    ok "hooks/hooks.json is valid"
  fi
  while IFS= read -r hook_script; do
    [ -z "$hook_script" ] && continue
    rel_path="${hook_script#"$REPO_ROOT"/}"
    if [ ! -x "$hook_script" ]; then
      err "$rel_path is not executable"
    else
      ok "$rel_path is executable"
    fi
  done <<< "$(find "$REPO_ROOT/hooks" -type f -name '*.sh' | sort)"

  if [ -f "$REPO_ROOT/scripts/test-guard.sh" ]; then
    if bash "$REPO_ROOT/scripts/test-guard.sh" > /dev/null 2>&1; then
      ok "guard test matrix passed (scripts/test-guard.sh)"
    else
      err "guard test matrix failed - run scripts/test-guard.sh for details"
    fi
  fi
fi

# --- Workflows (dynamic workflow scripts) ---
WORKFLOWS_DIR="$REPO_ROOT/workflows"
if [ -d "$WORKFLOWS_DIR" ]; then
  while IFS= read -r wf_file; do
    [ -z "$wf_file" ] && continue
    rel_path="${wf_file#"$REPO_ROOT"/}"
    if ! grep -q '^export const meta' "$wf_file"; then
      err "$rel_path missing 'export const meta' block"
      continue
    fi
    if command -v node >/dev/null 2>&1; then
      # Workflow scripts run as an async function body, so `node --check`
      # rejects valid ones. Parse as the runtime does; construct, never call.
      if ! node -e '
const fs = require("fs");
const src = fs.readFileSync(process.argv[1], "utf8").replace(/^export\s+const\s+meta/m, "const meta");
new Function("return (async () => {" + src + "\n})");
' "$wf_file" >/dev/null 2>&1; then
        err "$rel_path fails to parse as a workflow script body"
        continue
      fi
      ok "$rel_path parses as a workflow script body"
    else
      echo "WARN: node not found; $rel_path checked for meta block only" >&2
    fi
  done <<< "$(find "$WORKFLOWS_DIR" -type f -name '*.js' | sort)"
  if [ ! -x "$REPO_ROOT/scripts/install-workflow.sh" ]; then
    err "scripts/install-workflow.sh is missing or not executable"
  fi
fi

# --- OpenCode plugin ---
OPENCODE_DIR="$REPO_ROOT/opencode"
PACKAGE_JSON="$REPO_ROOT/package.json"
if [ -f "$PACKAGE_JSON" ]; then
  if ! python3 -m json.tool "$PACKAGE_JSON" > /dev/null 2>&1; then
    err "package.json is not valid JSON"
  else
    # One release, one version: drift would publish mismatched artefacts.
    pkg_version="$(python3 -c "import json; print(json.load(open('$PACKAGE_JSON')).get('version',''))")"
    plugin_version="$(python3 -c "import json; print(json.load(open('$PLUGIN_JSON')).get('version',''))")"
    if [ "$pkg_version" != "$plugin_version" ]; then
      err "package.json version ($pkg_version) does not match plugin.json version ($plugin_version)"
    else
      ok "package.json version matches plugin.json ($pkg_version)"
    fi

    pkg_main="$(python3 -c "import json; print(json.load(open('$PACKAGE_JSON')).get('main',''))")"
    if [ ! -f "$REPO_ROOT/${pkg_main#./}" ]; then
      err "package.json 'main' points at a missing file: $pkg_main"
    fi
  fi
fi

if [ -d "$OPENCODE_DIR" ]; then
  OPENCODE_PLUGIN="$OPENCODE_DIR/dotnet-episteme.js"
  OPENCODE_TEMPLATE="$OPENCODE_DIR/dotnet-review.template.md"

  if [ ! -f "$OPENCODE_PLUGIN" ]; then
    err "opencode/ exists but opencode/dotnet-episteme.js is missing"
  elif command -v node >/dev/null 2>&1; then
    if bash -c "cd '$REPO_ROOT' && node scripts/test-opencode-plugin.mjs" > /dev/null 2>&1; then
      ok "OpenCode plugin registers skills, agents, command, and MCP (scripts/test-opencode-plugin.mjs)"
    else
      err "OpenCode plugin registration test failed - run node scripts/test-opencode-plugin.mjs for details"
    fi
  else
    echo "WARN: node not found; OpenCode plugin registration not tested" >&2
  fi

  if [ ! -f "$OPENCODE_TEMPLATE" ]; then
    err "opencode/dotnet-review.template.md is missing"
  else
    for placeholder in '{{PLUGIN_ROOT}}' '{{TIER_GUIDANCE}}'; do
      if ! grep -qF "$placeholder" "$OPENCODE_TEMPLATE"; then
        err "opencode/dotnet-review.template.md no longer contains $placeholder - the plugin substitutes it"
      fi
    done
    frontmatter="$(awk '/^---$/{n++; next} n==1{print} n>=2{exit}' "$OPENCODE_TEMPLATE")"
    if ! printf '%s\n' "$frontmatter" | grep -q '^description:'; then
      err "opencode/dotnet-review.template.md missing frontmatter field: description"
    else
      ok "opencode/dotnet-review.template.md placeholders and frontmatter look valid"
    fi
  fi

  if [ ! -x "$REPO_ROOT/scripts/install-opencode.sh" ]; then
    err "scripts/install-opencode.sh is missing or not executable"
  fi

  # A comma-string `tools:` key makes OpenCode reject the whole document.
  if grep -rlE '^tools: *[A-Za-z]+,' "$OPENCODE_DIR" 2>/dev/null | grep -q .; then
    err "opencode/ contains a comma-string 'tools:' key - invalid in OpenCode's agent schema"
  else
    ok "opencode/ carries no Claude-only 'tools:' frontmatter"
  fi
fi

# --- Codex plugin ---
CODEX_MANIFEST="$REPO_ROOT/.codex-plugin/plugin.json"
if [ -f "$CODEX_MANIFEST" ]; then
  if ! python3 -m json.tool "$CODEX_MANIFEST" > /dev/null 2>&1; then
    err ".codex-plugin/plugin.json is not valid JSON"
  else
    # Rules from the published plugin.json spec.
    if ! CODEX_MANIFEST="$CODEX_MANIFEST" PLUGIN_JSON="$PLUGIN_JSON" REPO_ROOT="$REPO_ROOT" python3 - <<'PY'
import json
import os
import pathlib
import re
import sys

repo = pathlib.Path(os.environ["REPO_ROOT"])
manifest = json.loads(pathlib.Path(os.environ["CODEX_MANIFEST"]).read_text())
claude = json.loads(pathlib.Path(os.environ["PLUGIN_JSON"]).read_text())
errors = []

for field in ("name", "version", "description", "author"):
    if not manifest.get(field):
        errors.append(f"missing required field: {field}")
if not isinstance(manifest.get("author"), dict) or not manifest.get("author", {}).get("name"):
    errors.append("author.name is required")
if not re.fullmatch(r"[a-z0-9]+(-[a-z0-9]+)*", str(manifest.get("name", ""))):
    errors.append(f"name must be kebab-case: {manifest.get('name')!r}")
if not re.fullmatch(r"\d+\.\d+\.\d+([-+].*)?", str(manifest.get("version", ""))):
    errors.append(f"version must be strict semver: {manifest.get('version')!r}")
if "hooks" in manifest:
    errors.append("'hooks' fails Codex's marketplace validator (the runtime loader accepts it) - omit it; hooks/hooks.json is discovered automatically")
if manifest.get("version") != claude.get("version"):
    errors.append(f"version {manifest.get('version')} does not match plugin.json {claude.get('version')}")

for url_field in ("websiteURL", "privacyPolicyURL", "termsOfServiceURL"):
    url = (manifest.get("interface") or {}).get(url_field)
    if url is not None and not str(url).startswith("https://"):
        errors.append(f"interface.{url_field} must be an absolute https:// URL")

mcp = manifest.get("mcpServers")
if isinstance(mcp, str):
    if not (repo / mcp.lstrip("./")).is_file():
        errors.append(f"mcpServers path not found: {mcp}")
elif mcp is not None and not isinstance(mcp, dict):
    errors.append("mcpServers must be a path string or an object")

roots = manifest.get("skills")
for root in [roots] if isinstance(roots, str) else (roots or []):
    if not (repo / str(root).lstrip("./")).is_dir():
        errors.append(f"skills root not found: {root}")

for problem in errors:
    print(problem)
sys.exit(1 if errors else 0)
PY
    then
      err ".codex-plugin/plugin.json failed validation (see output above)"
    else
      ok ".codex-plugin/plugin.json is valid and version-aligned"
    fi
  fi

  if [ ! -x "$REPO_ROOT/scripts/install-codex.sh" ]; then
    err "scripts/install-codex.sh is missing or not executable"
  fi

  # This skill lives outside the shared skills root, so the loop above misses it.
  while IFS= read -r codex_skill; do
    [ -z "$codex_skill" ] && continue
    rel_path="${codex_skill#"$REPO_ROOT"/}"
    frontmatter="$(awk '/^---$/{n++; next} n==1{print} n>=2{exit}' "$codex_skill")"
    for field in name description; do
      if ! printf '%s\n' "$frontmatter" | grep -q "^${field}:"; then
        err "$rel_path missing frontmatter field: $field"
      fi
    done
    ok "$rel_path frontmatter looks valid"
  done <<< "$(find "$REPO_ROOT/codex" -type f -name 'SKILL.md' 2>/dev/null | sort)"
fi

# --- Monitors (experimental) ---
MONITORS_JSON="$REPO_ROOT/monitors/monitors.json"
if [ -f "$MONITORS_JSON" ]; then
  if ! python3 -c "
import json, sys
monitors = json.load(open('$MONITORS_JSON'))
ok = isinstance(monitors, list) and all(
    isinstance(m, dict) and all(k in m for k in ('name', 'command', 'description'))
    for m in monitors)
sys.exit(0 if ok else 1)" 2>/dev/null; then
    err "monitors/monitors.json must be a JSON array of {name, command, description} entries"
  else
    ok "monitors/monitors.json is valid"
  fi
fi

echo
if [ "$ERRORS" -gt 0 ]; then
  echo "Validation failed with $ERRORS error(s)."
  exit 1
fi

echo "Validation passed."
