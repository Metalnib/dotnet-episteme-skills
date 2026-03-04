#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || dirname "$(dirname "$0")")"
PLUGIN_JSON="$REPO_ROOT/.claude-plugin/plugin.json"
MARKETPLACE_JSON="$REPO_ROOT/.claude-plugin/marketplace.json"
ERRORS=0

err() { echo "ERROR: $*" >&2; ERRORS=$((ERRORS + 1)); }
ok()  { echo "OK: $*"; }

if [ ! -f "$MARKETPLACE_JSON" ]; then
  err "Missing $MARKETPLACE_JSON"
else
  if ! python3 -m json.tool "$MARKETPLACE_JSON" > /dev/null 2>&1; then
    err "marketplace.json is not valid JSON"
  else
    ok "marketplace.json syntax is valid"
  fi
fi

if [ ! -f "$PLUGIN_JSON" ]; then
  err "Missing $PLUGIN_JSON"
else
  if ! python3 -m json.tool "$PLUGIN_JSON" > /dev/null 2>&1; then
    err "plugin.json is not valid JSON"
  else
    ok "plugin.json syntax is valid"
  fi
fi

for key in name description owner repository plugins; do
  if ! python3 -c "import json,sys; d=json.load(open('$MARKETPLACE_JSON')); sys.exit(0 if '$key' in d else 1)" 2>/dev/null; then
    err "marketplace.json missing required key: $key"
  fi
done

plugin_name="$(python3 -c "import json; print(json.load(open('$PLUGIN_JSON')).get('name',''))" 2>/dev/null || true)"
plugin_version="$(python3 -c "import json; print(json.load(open('$PLUGIN_JSON')).get('version',''))" 2>/dev/null || true)"
plugin_repository="$(python3 -c "import json; print(json.load(open('$PLUGIN_JSON')).get('repository',''))" 2>/dev/null || true)"

marketplace_plugin_name="$(python3 -c "import json; d=json.load(open('$MARKETPLACE_JSON')); p=(d.get('plugins') or [{}])[0]; print(p.get('name',''))" 2>/dev/null || true)"
marketplace_plugin_version="$(python3 -c "import json; d=json.load(open('$MARKETPLACE_JSON')); p=(d.get('plugins') or [{}])[0]; print(p.get('version',''))" 2>/dev/null || true)"
marketplace_source="$(python3 -c "import json; d=json.load(open('$MARKETPLACE_JSON')); p=(d.get('plugins') or [{}])[0]; print(p.get('source',''))" 2>/dev/null || true)"
marketplace_repository="$(python3 -c "import json; print(json.load(open('$MARKETPLACE_JSON')).get('repository',''))" 2>/dev/null || true)"

if [ "$marketplace_plugin_name" != "$plugin_name" ]; then
  err "plugins[0].name ($marketplace_plugin_name) must match plugin.json name ($plugin_name)"
else
  ok "plugin name alignment looks valid"
fi

if [ "$marketplace_plugin_version" != "$plugin_version" ]; then
  err "plugins[0].version ($marketplace_plugin_version) must match plugin.json version ($plugin_version)"
else
  ok "plugin version alignment looks valid"
fi

if [ "$marketplace_source" != "./" ]; then
  err "plugins[0].source must be ./ (got: $marketplace_source)"
else
  ok "plugin source looks valid"
fi

if [ "$marketplace_repository" != "$plugin_repository" ]; then
  err "marketplace repository ($marketplace_repository) must match plugin repository ($plugin_repository)"
else
  ok "repository alignment looks valid"
fi

echo
if [ "$ERRORS" -gt 0 ]; then
  echo "Marketplace validation failed with $ERRORS error(s)."
  exit 1
fi

echo "Marketplace validation passed."
