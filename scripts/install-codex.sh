#!/usr/bin/env bash
# Registers the thirteen worker roles (review, refactor, qa) with Codex and
# pre-warms the Synopsis binary.
# Roles cannot ship inside a plugin: Codex only reads them from config.toml.
#
#   scripts/install-codex.sh              register roles (and pre-warm)
#   scripts/install-codex.sh --verify     report what is registered
#   scripts/install-codex.sh --uninstall  remove the managed block and role files
set -euo pipefail

ROOT="$(cd "$(dirname "$(dirname "$0")")" && pwd)"
CODEX_HOME="${CODEX_HOME:-$HOME/.codex}"
CONFIG="$CODEX_HOME/config.toml"
ROLE_DIR="$CODEX_HOME/agents/dotnet-episteme"
BEGIN_MARKER="# >>> dotnet-episteme-skills (managed) - edits inside this block are overwritten"
END_MARKER="# <<< dotnet-episteme-skills (managed)"
ROLES="review-correctness review-performance review-security-observability review-data-messaging review-generalist review-maintainer refactor-cartographer refactor-tracer refactor-conformance-auditor refactor-surveyor qa-acceptance qa-reuse-design qa-dead-code"
MODE="install"

case "${1:-}" in
  --verify) MODE="verify" ;;
  --uninstall) MODE="uninstall" ;;
  "") ;;
  *) echo "Unknown argument: $1" >&2; exit 2 ;;
esac

# Print config.toml without the managed block; silent when the file is absent.
strip_block() {
  [ -f "$CONFIG" ] || return 0
  BEGIN="$BEGIN_MARKER" END="$END_MARKER" python3 - "$CONFIG" <<'PY'
import os, sys

begin, end = os.environ["BEGIN"], os.environ["END"]
keep, skipping = [], False
for line in open(sys.argv[1], encoding="utf-8"):
    stripped = line.rstrip("\n")
    if stripped == begin:
        skipping = True
    elif stripped == end:
        skipping = False
    elif not skipping:
        keep.append(line)
text = "".join(keep).rstrip("\n")
sys.stdout.write(text + "\n" if text else "")
PY
}

if [ "$MODE" = "uninstall" ]; then
  if [ -f "$CONFIG" ]; then
    strip_block > "$CONFIG.tmp" && mv "$CONFIG.tmp" "$CONFIG"
    echo "Removed the managed [agents] block from $CONFIG"
  fi
  rm -rf "$ROLE_DIR"
  echo "Removed $ROLE_DIR"
  echo "The plugin is untouched: codex plugin remove dotnet-episteme-skills@dotnet-episteme-marketplace"
  exit 0
fi

if [ "$MODE" = "install" ]; then
  for group in review refactor qa; do
    [ -d "$ROOT/agents/$group" ] || { echo "ERROR: $ROOT/agents/$group not found" >&2; exit 1; }
  done
  mkdir -p "$ROLE_DIR" "$CODEX_HOME"
  BLOCK_FILE="$(mktemp)"
  trap 'rm -f "$BLOCK_FILE" "$CONFIG.tmp"' EXIT

  # Worker prompts come from agents/{review,refactor,qa}/*.md so all tools share one copy.
  ROOT="$ROOT" ROLE_DIR="$ROLE_DIR" BLOCK_OUT="$BLOCK_FILE" python3 - <<'PY'
import os, pathlib, sys

root = pathlib.Path(os.environ["ROOT"])
roles = pathlib.Path(os.environ["ROLE_DIR"])
block = []

# Codex has no plugin commands; per-group pipeline skills drive the workers.
SKILL_FOR_COMMAND = {
    "the dotnet-review command": "the review-pipeline skill",
    "the dotnet-refactor command": "the refactor-pipeline skill",
    "the dotnet-qa command": "the qa-pipeline skill",
}

for group in ("review", "refactor", "qa"):
    for src in sorted((root / "agents" / group).glob("*.md")):
        lines = src.read_text(encoding="utf-8").split("\n")
        if lines[0].strip() != "---":
            sys.exit(f"{src} has no YAML frontmatter")
        end = next((i for i, line in enumerate(lines[1:], 1) if line.strip() == "---"), None)
        if end is None:
            sys.exit(f"{src} has an unterminated YAML frontmatter block")

        front = lines[1:end]
        body = "\n".join(lines[end + 1:]).strip()
        description = next(
            (line[len("description:"):].strip().strip('"') for line in front if line.startswith("description:")),
            "",
        )
        if not body or not description:
            sys.exit(f"{src} is missing a description or a body")

        for command, skill in SKILL_FOR_COMMAND.items():
            description = description.replace(command, skill)

        name = f"{group}-{src.stem}"
        instructions = roles / f"{name}.md"
        layer = roles / f"{name}.toml"
        instructions.write_text(body + "\n", encoding="utf-8")
        layer.write_text(
            f'model_instructions_file = "{instructions}"\n'
            # The guard cannot scope to a role on Codex, so the sandbox enforces this.
            'sandbox_mode = "read-only"\n',
            encoding="utf-8",
        )
        escaped = description.replace("\\", "\\\\").replace('"', '\\"')
        block.append(f'[agents.{name}]\ndescription = "{escaped}"\nconfig_file = "{layer}"\n')

pathlib.Path(os.environ["BLOCK_OUT"]).write_text("\n".join(block), encoding="utf-8")
print(f"Wrote {len(block)} role layers to {roles}")
PY

  # Without this the five reviewers run in waves. Emitting a second [agents]
  # header would be a TOML error, so skip when the user already has one.
  if [ -f "$CONFIG" ] && strip_block | grep -qE '^\[agents\]|^agents\.max_concurrent_threads_per_session'; then
    echo "NOTE  you already configure [agents] yourself - leaving concurrency alone."
    echo "      For a full parallel fan-out set: agents.max_concurrent_threads_per_session = 6"
  else
    printf '[agents]\nmax_concurrent_threads_per_session = 6\n\n' | cat - "$BLOCK_FILE" > "$BLOCK_FILE.tmp"
    mv "$BLOCK_FILE.tmp" "$BLOCK_FILE"
    echo "Set agents.max_concurrent_threads_per_session = 6 (five reviewers + maintainer)"
  fi

  {
    strip_block
    echo
    echo "$BEGIN_MARKER"
    cat "$BLOCK_FILE"
    echo "$END_MARKER"
  } > "$CONFIG.tmp"
  mv "$CONFIG.tmp" "$CONFIG"
  echo "Registered the worker roles in $CONFIG"

  # A cold download would exceed the MCP startup window.
  DETECT="$ROOT/skills/dotnet-techne-synopsis/scripts/detect-tool.sh"
  if [ -x "$DETECT" ] && binary="$("$DETECT" 2>/dev/null)"; then
    echo "Synopsis binary ready: $binary"
  else
    echo "WARNING: no Synopsis binary resolved; the MCP server may fail its first connect." >&2
  fi
fi

echo
echo "Verifying:"
FAILURES=0

if [ ! -f "$CONFIG" ]; then
  echo "  FAIL  $CONFIG does not exist"
  exit 1
fi

for role in $ROLES; do
  if grep -q "^\[agents\.$role\]$" "$CONFIG" &&
     [ -f "$ROLE_DIR/$role.toml" ] &&
     [ -f "$ROLE_DIR/$role.md" ]; then
    echo "  OK    $role registered"
  else
    echo "  FAIL  $role missing"
    FAILURES=$((FAILURES + 1))
  fi
done

if command -v codex >/dev/null 2>&1; then
  # Any config-reading command doubles as a syntax check.
  if codex mcp list >/dev/null 2>&1; then
    echo "  OK    codex parses config.toml"
  else
    echo "  FAIL  codex cannot parse config.toml - run: codex mcp list"
    FAILURES=$((FAILURES + 1))
  fi

  if codex plugin list 2>/dev/null | grep -q "dotnet-episteme-skills.*installed"; then
    echo "  OK    plugin installed (skills, Synopsis MCP, read-only git guard)"
  else
    echo "  NOTE  plugin not installed yet - run:"
    echo "          codex plugin marketplace add Metalnib/dotnet-episteme-skills"
    echo "          codex plugin add dotnet-episteme-skills@dotnet-episteme-marketplace"
  fi
else
  echo "  NOTE  codex not on PATH - skipped CLI checks"
fi

echo
if [ "$FAILURES" -gt 0 ]; then
  echo "$FAILURES check(s) failed."
  exit 1
fi
echo 'Done. Ask Codex for a multi-agent .NET review, story QA, or a design loop; it selects the dotnet-techne-review-pipeline, dotnet-techne-qa-pipeline, or dotnet-techne-refactor-pipeline skill.'
