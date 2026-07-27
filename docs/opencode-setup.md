# Using these skills in OpenCode

Needs OpenCode 1.18 or newer.

## Install

```bash
git clone https://github.com/Metalnib/dotnet-episteme-skills.git
cd dotnet-episteme-skills
scripts/install-opencode.sh
```

The script links the plugin into `~/.config/opencode/plugin/` and checks the result, so `git pull` is all an update takes. It prints what it registered:

```text
  OK    skills path registered
  OK    six review subagents registered
  OK    /dotnet-review command registered
  OK    Synopsis MCP server registered
```

`--verify` re-runs those checks, `--uninstall` removes the link.

An `npm` one-liner (`opencode plugin opencode-dotnet-episteme -g`) is planned but not published yet.

## Using it

- **The 10 skills**: describe what you need and OpenCode picks the right one, or call it by name.
- **A full review**: `/dotnet-review` with a branch, a commit range, `--staged`, or nothing for your current changes. Add `--cynical` for a harsher pass. Five reviewers run in parallel, then a sixth tries to disprove their findings.
- **Dependency questions** ("what breaks if I change this?"): ask, and the Synopsis graph tools answer.

Reviewers cannot edit files or browse the web, and they can only run read-only `git` commands.

## Stronger models for big changes

OpenCode cannot pick a model per reviewer, so every reviewer uses your current session model. Name a stronger model and you get a second set of reviewers pinned to it:

```bash
DOTNET_EPISTEME_STRONG_MODEL=anthropic/claude-opus-5 opencode
```

`/dotnet-review` then uses those when the change is big or touches security, public APIs, or data.

## Checking the install

```bash
opencode agent list      # six review-* subagents
opencode mcp list        # synopsis: connected
opencode debug config    # skills path, agents, command, MCP
```

## Careful when editing OpenCode's config by hand

OpenCode accepts two styles of config file and picks one per file. Mixing them makes it **drop the whole file silently** — your models and servers simply disappear, with no error. So keep the style the file already uses, and run `opencode debug config` after any edit to confirm it still loads.

Also: never copy this repo's `agents/review/*.md` into OpenCode's agent folder. Those files are written for Claude Code, and OpenCode rejects them in a way that breaks its entire config. The plugin converts them for you.

## Good to know

- **Windows**: the graph server starts through a shell script, so run OpenCode under WSL2 to get it. Skills, reviewers and the command work on Windows as they are.
- `/dotnet-review` drives the reviewers itself, so their findings pass through the main conversation.
- The experimental log monitor works in Claude Code only.

## If something goes wrong

| What you see | What to do |
|---|---|
| `Error: Configuration is invalid at …/agent/<name>.md` | A Claude Code agent file was copied into OpenCode's agent folder. Delete it. |
| Models and providers vanished after a config edit | Mixed config styles — restore the file and check with `opencode debug config`. |
| `/dotnet-review` missing | The plugin is not loaded: check the link in `~/.config/opencode/plugin/`. |
| No Synopsis tools | `opencode mcp list`. The first run downloads a binary — check `~/.synopsis/synopsis.log`. |
| No skills found even though the path is registered | Don't pipe `opencode debug skill` — it truncates. Redirect to a file. |
