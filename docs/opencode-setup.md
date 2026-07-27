# OpenCode setup

OpenCode cannot consume Claude Code plugins, so this repo ships a native OpenCode plugin instead: `opencode/dotnet-episteme.js`. It registers the same four things the Claude Code plugin does — the skills, the review subagents, the `/dotnet-review` command, and the Synopsis MCP server — by mutating OpenCode's config from its `config` hook.

Verified against OpenCode **1.18.7** on macOS.

## Install

### From npm (one command)

> Available once `opencode-dotnet-episteme` is published; until then use the clone install below. Publishing is enabled by adding an `NPM_TOKEN` repository secret — see *Releasing* in the README.

```bash
opencode plugin opencode-dotnet-episteme -g
```

That installs the package and writes the plugin entry into your global config; OpenCode caches npm plugins under `~/.cache/opencode/node_modules/`. Restart OpenCode and the skills, the six `review-*` subagents, `/dotnet-review`, and the Synopsis MCP server are all registered.

With this path you can also configure the plugin, which the file-based install cannot do (options only reach plugins declared in config):

```jsonc
// v1-shaped config (a document containing provider/agent/command/permission keys)
{ "plugin": [["opencode-dotnet-episteme", { "strongModel": "anthropic/claude-opus-5" }]] }

// v2-shaped config
{ "plugins": [{ "package": "opencode-dotnet-episteme", "options": { "strongModel": "anthropic/claude-opus-5" } }] }
```

One caveat: Synopsis downloads its binary into the package directory inside OpenCode's npm cache, so a cache prune costs you a re-download. Put `synopsis` on your `PATH` (the detect script prefers it) if you want that to be permanent.

### From a clone (symlink, best for contributors)

```bash
git clone https://github.com/Metalnib/dotnet-episteme-skills.git
cd dotnet-episteme-skills
scripts/install-opencode.sh
```

The script symlinks the plugin into `~/.config/opencode/plugin/` and then verifies the install through the OpenCode CLI:

```text
Linked /Users/you/.config/opencode/plugin/dotnet-episteme.js -> /path/to/repo/opencode/dotnet-episteme.js

Verifying with the OpenCode CLI:
  OK    skills path registered
  OK    six review subagents registered
  OK    /dotnet-review command registered
  OK    Synopsis MCP server registered
```

Because it is a symlink and the plugin resolves its own location, `git pull` is the whole update path — no copying, no config edits. `scripts/install-opencode.sh --verify` re-runs the checks; `--uninstall` removes the symlink.

Manual equivalent, if you prefer:

```bash
mkdir -p ~/.config/opencode/plugin
ln -s "$PWD/opencode/dotnet-episteme.js" ~/.config/opencode/plugin/dotnet-episteme.js
```

## What you get

| Surface | Name in OpenCode |
|---|---|
| 10 skills | `dotnet-techne-*` (model-invocable, and `skill` tool by name) |
| 5 reviewers + maintainer | `review-correctness`, `review-performance`, `review-security-observability`, `review-data-messaging`, `review-generalist`, `review-maintainer` |
| Orchestrator command | `/dotnet-review [branch\|commit-range\|--staged] [--cynical]` |
| Synopsis graph tools | MCP server `synopsis`, rooted at the current workspace |

The reviewers are read-only by construction: each is registered with `edit: deny`, `webfetch: deny`, and a bash allowlist of `git diff|log|show|blame|status|rev-parse|merge-base`. This replaces `hooks/git-readonly-guard.sh` (OpenCode has JS plugin hooks, not shell hooks) and is in one respect stricter: the allow patterns anchor on the subcommand, so `git -C /some/other/repo diff` matches no allow rule and is denied outright. OpenCode also parses chained commands, so `git status && rm -rf x` is denied by the catch-all.

## Verify by hand

```bash
opencode debug skill > /tmp/skills.json   # NB: redirect, do not pipe (see below)
opencode agent list                       # the six review-* subagents
opencode debug agent review-correctness   # resolved permission rules
opencode mcp list                         # synopsis: connected
opencode debug config                     # skills.paths, agent, command, mcp
```

> `opencode debug skill` truncates at about 64 KB when its stdout is a pipe. Redirect to a file before parsing, or you will conclude that nothing is registered.

## Optional: restore model tiering

OpenCode's `task` tool takes `description`, `prompt`, `subagent_type`, `task_id` and `background` — there is **no per-invocation `model`**. The size-to-tier table in `/dotnet-review` therefore has no delivery mechanism by default: every reviewer runs on the session model, and the command says so instead of silently under-reviewing.

To get tiering back, name a strong model and the plugin registers a second, pinned variant of every lane — via plugin options (npm install) or an env var (either install):

```bash
DOTNET_EPISTEME_STRONG_MODEL=anthropic/claude-opus-5 opencode
```

```text
review-correctness-strong (subagent)   → pinned to anthropic/claude-opus-5
review-maintainer-strong  (subagent)   → …
```

The command text adapts: when the variants exist it is told to dispatch `review-<lane>-strong` whenever the sizing table calls for a stronger tier. If you install the plugin as an npm package you can pass `{ strongModel: "…" }` as plugin options instead of an env var — options do not reach plugins that were dropped into the plugin directory as files.

## Do not copy `agents/review/*.md` into OpenCode

Those files are Claude Code agents. Their `tools: Read, Grep, Glob, Bash` frontmatter is a comma string where OpenCode's schema wants a map, and OpenCode fails the **whole config document** over it:

```text
$ cp agents/review/correctness.md ~/.config/opencode/agent/
$ opencode agent list
Error: Configuration is invalid at ~/.config/opencode/agent/correctness.md
```

That is not limited to the offending agent — config resolution for the session dies with it. The plugin exists precisely so that these files stay Claude-native and single-sourced: it reads their bodies at load time and registers them with OpenCode-shaped metadata. (`agents/review/data-messaging.md` uses `disallowedTools:` instead, which OpenCode merely ignores — it would load, but as a primary-and-subagent `all` agent named from its frontmatter rather than its filename.)

## Config shape: v1 and v2

OpenCode 1.18.x accepts two config schemas and sniffs each document: if it contains **any** of `logLevel, server, command, reference, snapshot, plugin, autoshare, disabled_providers, enabled_providers, small_model, mode, agent, provider, permission, tools, attachment, layout`, the whole file is read as **v1** and migrated in memory. Otherwise it is read as **v2**.

| v1 | v2 |
|---|---|
| `mcp.<name>` + `enabled: true` | `mcp.servers.<name>` + `disabled: false` |
| `agent.<name>` with `prompt`, `permission` (object) | `agents.<name>` with `system`, `permissions` (rule array) |
| `command.<name>` | `commands.<name>` |
| `skills.paths[]` / `skills.urls[]` | `skills[]` (one flat array) |
| `plugin[]` as `[pkg, opts]` | `plugins[]` as `{ package, options }` |
| `provider.<id>` | `providers.<id>` |

Two failure modes worth knowing before you hand-edit anything:

1. **A v1 document plus one v2-shaped key discards the entire file, silently.** Adding `skills: ["/path"]` to a config that also has `provider` fails the v1 decode; the loader returns nothing and every provider, model and MCP server you configured disappears with no error. Always re-check `opencode debug config` after editing.
2. **v2-only keys in a v1 document are ignored**, so a snippet copied from newer docs can appear to do nothing.

You do not need to pick a side for this plugin: it writes v1 keys, which is the shape OpenCode 1.18.x hands to the `config` hook, and warns on stderr if a future version hands it a v2-shaped config instead.

## Limits compared with Claude Code

- **No workflow runtime.** `workflows/dotnet-review.js` has no OpenCode equivalent, so the fan-out, dedupe and maintainer pass are driven by the model through parallel `task` calls. Reviewer findings therefore pass through the orchestrator's context instead of being merged in code.
- **No log monitor.** `monitors/` is Claude Code specific. OpenCode's `event` / `session.*` hooks are a plausible port target; not done.
- **Windows.** The Synopsis MCP launcher is a POSIX shell script, so the plugin skips MCP registration on native Windows (skills, agents and the command still work). Under WSL2 everything works — OpenCode reports `linux` there.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `Error: Configuration is invalid at …/agent/<x>.md` | A Claude agent file with a comma-string `tools:` key is in OpenCode's agent dir. Delete it. |
| Providers/models vanished after editing config | v1/v2 key mixing — see above. Restore and re-check `opencode debug config`. |
| No skills found, yet the path is registered | You piped `opencode debug skill`; redirect to a file. |
| `/dotnet-review` missing | Plugin not loaded: check the symlink in `~/.config/opencode/plugin/`, then `opencode debug config`. |
| Synopsis tools missing | `opencode mcp list`; the launcher auto-downloads the binary on first run — see `~/.synopsis/synopsis.log`. |
