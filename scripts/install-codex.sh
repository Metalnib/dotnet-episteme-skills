#!/usr/bin/env bash
# Registers the six review roles with Codex and pre-warms the Synopsis binary.
#
# Skills, the Synopsis MCP server, and the read-only git guard come from the
# plugin itself (`codex plugin add dotnet-episteme-skills@dotnet-episteme-marketplace`).
# Agent roles cannot ship inside a plugin - Codex reads them from the [agents]
# table in config.toml - so this script writes them, from either a clone or the
# installed plugin cache.
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
LANES="correctness performance security-observability data-messaging generalist maintainer"
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
  [ -d "$ROOT/agents/review" ] || { echo "ERROR: $ROOT/agents/review not found" >&2; exit 1; }
  mkdir -p "$ROLE_DIR" "$CODEX_HOME"
  BLOCK_FILE="$(mktemp)"
  trap 'rm -f "$BLOCK_FILE" "$CONFIG.tmp"' EXIT

  # One instruction file plus one TOML config layer per lane. The reviewer prompt
  # is the body of the Claude agent file - same single source, frontmatter dropped,
  # because Codex takes instructions from a file and everything else from TOML keys.
  ROOT="$ROOT" ROLE_DIR="$ROLE_DIR" BLOCK_OUT="$BLOCK_FILE" python3 - <<'PY'
import os, pathlib, sys

root = pathlib.Path(os.environ["ROOT"])
roles = pathlib.Path(os.environ["ROLE_DIR"])
block = []

for src in sorted((root / "agents" / "review").glob("*.md")):
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

    # Codex shows this description when choosing a role, and it has no
    # /dotnet-review command - the review-pipeline skill drives the fan-out.
    description = description.replace("the dotnet-review command", "the review-pipeline skill")

    name = f"review-{src.stem}"
    instructions = roles / f"{name}.md"
    layer = roles / f"{name}.toml"
    instructions.write_text(body + "\n", encoding="utf-8")
    layer.write_text(
        f'model_instructions_file = "{instructions}"\n'
        # Reviewers must never write. On Codex the plugin's PreToolUse guard cannot
        # scope itself to a role, so the sandbox carries the restriction instead.
        'sandbox_mode = "read-only"\n',
        encoding="utf-8",
    )
    escaped = description.replace("\\", "\\\\").replace('"', '\\"')
    block.append(f'[agents.{name}]\ndescription = "{escaped}"\nconfig_file = "{layer}"\n')

pathlib.Path(os.environ["BLOCK_OUT"]).write_text("\n".join(block), encoding="utf-8")
print(f"Wrote {len(block)} role layers to {roles}")
PY

  # Codex defaults to fewer concurrent agent threads than this pipeline needs, so
  # five "parallel" reviewers actually run in waves. Only emit the [agents] header
  # when the user has none of their own - two headers for one table is a TOML error.
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
  echo "Registered the review roles in $CONFIG"

  # Codex gives an MCP server a bounded startup window; downloading the binary
  # inside it would blow the budget on a cold cache.
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

for lane in $LANES; do
  if grep -q "^\[agents\.review-$lane\]$" "$CONFIG" &&
     [ -f "$ROLE_DIR/review-$lane.toml" ] &&
     [ -f "$ROLE_DIR/review-$lane.md" ]; then
    echo "  OK    review-$lane registered"
  else
    echo "  FAIL  review-$lane missing"
    FAILURES=$((FAILURES + 1))
  fi
done

if command -v codex >/dev/null 2>&1; then
  # A malformed config.toml breaks every codex command, so any config-reading
  # command doubles as a syntax check.
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
echo 'Done. Ask Codex for a multi-agent .NET review; it selects the dotnet-techne-review-pipeline skill.'
