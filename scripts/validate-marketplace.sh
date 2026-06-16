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

for key in name owner plugins; do
  if ! python3 -c "import json,sys; d=json.load(open('$MARKETPLACE_JSON')); sys.exit(0 if '$key' in d else 1)" 2>/dev/null; then
    err "marketplace.json missing required key: $key"
  fi
done

plugin_name="$(python3 -c "import json; print(json.load(open('$PLUGIN_JSON')).get('name',''))" 2>/dev/null || true)"
plugin_version="$(python3 -c "import json; print(json.load(open('$PLUGIN_JSON')).get('version',''))" 2>/dev/null || true)"

if ! python3 -c "import json,sys; d=json.load(open('$MARKETPLACE_JSON')); p=d.get('plugins'); sys.exit(0 if isinstance(p,list) and len(p)>0 else 1)" 2>/dev/null; then
  err "marketplace.json field 'plugins' must be a non-empty array"
fi

if ! python3 -c "import json,sys; d=json.load(open('$MARKETPLACE_JSON')); sys.exit(0 if any((isinstance(p,dict) and 'name' in p and 'source' in p) for p in d.get('plugins',[])) else 1)" 2>/dev/null; then
  err "marketplace.json must include at least one plugin entry with both 'name' and 'source'"
fi

marketplace_plugin_name="$(python3 -c "import json; d=json.load(open('$MARKETPLACE_JSON')); matches=[p for p in d.get('plugins',[]) if isinstance(p,dict) and p.get('name')=='$plugin_name']; p=(matches[0] if matches else {}); print(p.get('name',''))" 2>/dev/null || true)"
marketplace_plugin_version="$(python3 -c "import json; d=json.load(open('$MARKETPLACE_JSON')); matches=[p for p in d.get('plugins',[]) if isinstance(p,dict) and p.get('name')=='$plugin_name']; p=(matches[0] if matches else {}); print(p.get('version',''))" 2>/dev/null || true)"
marketplace_source="$(python3 -c "import json; d=json.load(open('$MARKETPLACE_JSON')); matches=[p for p in d.get('plugins',[]) if isinstance(p,dict) and p.get('name')=='$plugin_name']; p=(matches[0] if matches else {}); print(p.get('source',''))" 2>/dev/null || true)"

if [ -z "$marketplace_plugin_name" ]; then
  err "No plugin entry named '$plugin_name' found in marketplace.json"
else
  ok "plugin name alignment looks valid"
fi

if [ "$marketplace_plugin_version" != "$plugin_version" ]; then
  err "Plugin '$plugin_name' version in marketplace.json ($marketplace_plugin_version) must match plugin.json version ($plugin_version)"
else
  ok "plugin version alignment looks valid"
fi

if [ "$marketplace_source" != "./" ]; then
  err "Plugin '$plugin_name' source must be ./ (got: $marketplace_source)"
else
  ok "plugin source looks valid"
fi

echo
if [ "$ERRORS" -gt 0 ]; then
  echo "Marketplace validation failed with $ERRORS error(s)."
  exit 1
fi

echo "Marketplace validation passed."
