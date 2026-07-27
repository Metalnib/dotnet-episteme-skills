# Codex setup

Codex installs this repo as a plugin. Everything except the agent roles ships inside it: the skills, the Synopsis MCP server, and the read-only git guard.

Verified against **codex-cli 0.145.0** on macOS.

## Install

```bash
codex plugin marketplace add Metalnib/dotnet-episteme-skills
codex plugin add dotnet-episteme-skills@dotnet-episteme-marketplace
```

That gives you 11 skills, the `synopsis` MCP server, and the `PreToolUse` guard. Then register the review roles, which a plugin cannot carry (Codex reads them from `config.toml`):

```bash
# from a clone
scripts/install-codex.sh

# or from the installed plugin, without cloning
~/.codex/plugins/cache/dotnet-episteme-marketplace/dotnet-episteme-skills/*/scripts/install-codex.sh
```

```text
Wrote 6 role layers to ~/.codex/agents/dotnet-episteme
Registered the review roles in ~/.codex/config.toml
Synopsis binary ready: …/skills/dotnet-techne-synopsis/bin/osx-arm64/synopsis

Verifying:
  OK    review-correctness registered
  …
  OK    codex parses config.toml
  OK    plugin installed (skills, Synopsis MCP, read-only git guard)
```

The script is idempotent (it owns a marked block in `config.toml`), takes `--verify` and `--uninstall`, and pre-warms the Synopsis binary so the MCP server never races a download against its startup window.

**First run asks about hooks.** Codex shows `1 hook needs review before it can run`; choose *Trust all and continue*. The decision is recorded in `[hooks.state]` keyed by `<plugin>@<marketplace>:hooks/hooks.json:pre_tool_use:0:0` with a `trusted_hash`. That hash covers the hook **entry in `hooks.json`**, not the script it points at: editing `hooks/git-readonly-guard.sh` does not re-prompt (measured — the guard changed and the hook stayed `Active 1`). Re-review the script yourself when you update it.

## What you get

| Surface | In Codex |
|---|---|
| 10 `dotnet-techne-*` skills | `@dotnet-techne-…` or auto-selected; namespaced `dotnet-episteme-skills:dotnet-techne-*` |
| Multi-agent review | the `dotnet-techne-review-pipeline` skill drives the fan-out (Codex has no custom slash commands) |
| 5 reviewers + maintainer | roles `review-correctness`, `review-performance`, `review-security-observability`, `review-data-messaging`, `review-generalist`, `review-maintainer` |
| Synopsis graph tools | MCP server `synopsis`, rooted at the workspace, state in `~/.synopsis/state` |
| Read-only reviewers | each role's config layer sets `sandbox_mode = "read-only"` |

Start a review by asking for one — "run a multi-agent review of this branch" — and Codex selects the pipeline skill, which lists the roles, fans out, merges, and runs the maintainer pass. There is no `/dotnet-review` on Codex: `~/.codex/prompts` is not a feature of 0.145.0, so a skill is the native way to ship an orchestrator.

## Why the design looks like this

Three Codex behaviours shaped it, all verified rather than assumed:

1. **`${CLAUDE_PLUGIN_ROOT}` is never expanded in an MCP command.** Codex stores the string literally and spawns it directly, so the Claude-shaped `.mcp.json` fails with `MCP startup failed: No such file or directory`. Plugin-spawned MCP processes also get **no** `PLUGIN_ROOT`/`CLAUDE_PLUGIN_ROOT` in their environment - only hooks do. So `.codex-plugin/plugin.json` points `mcpServers` at `codex/mcp.json`, which launches the shared launcher through a shell that resolves it from `$HOME`:

   ```json
   { "command": "bash",
     "args": ["-c", "exec \"$(ls -dt \"$HOME\"/.codex/plugins/cache/*/dotnet-episteme-skills/*/bin/synopsis-mcp-launcher.sh | head -1)\""],
     "startup_timeout_sec": 120 }
   ```

   Newest cache entry wins (`ls -dt`), so a version bump needs no config change, and the launcher keeps its own `--state-dir`/`--log-file` handling. Without `--state-dir` Synopsis silently falls back to an in-memory store, losing incremental state between sessions.

2. **Hooks do get the plugin environment, and the shell expands it.** `hooks/hooks.json` works unchanged, and `tool_name` arrives as `Bash` - Codex maps its shell tool to that name, so the existing `matcher` matches. The guard is, however, **inert on Codex**: `PreToolUse` carries `session_id`, `turn_id`, `tool_name`, `tool_input` and `permission_mode` but **no `agent_type`** (only `SubagentStart`/`SubagentStop` carry that), so it cannot tell a reviewer from the main session and deliberately exits 0 rather than restricting everything. Reviewer read-only enforcement therefore lives in each role's `sandbox_mode`.

3. **Plugins cannot ship agent roles or slash commands.** The manifest spec accepts `skills`, `mcpServers` and `apps`, and *rejects* a `hooks` field (hooks are discovered from `hooks.json`). Roles come from the `[agents]` table, which is why `scripts/install-codex.sh` exists. Each role gets a TOML layer with `model_instructions_file` pointing at a frontmatter-stripped copy of the same `agents/review/*.md` body Claude Code uses - one source, three tools.

## Verify by hand

```bash
codex plugin list                  # installed, enabled
codex mcp get synopsis             # command + startup_timeout_sec
codex debug prompt-input           # skills reaching the model
scripts/install-codex.sh --verify  # roles + config parse
```

In the TUI: `/skills` lists the plugin's skills, `/hooks` should show `PreToolUse  Installed 1  Active 1`, and asking Codex to list its agent roles should return the six `review-*` names alongside `default`, `explorer` and `worker`.

## Cost and concurrency

A full pipeline run spends **six agent turns** - five reviewers plus the maintainer - on top of the orchestrator's own context. On a metered or free plan that is easily the most expensive thing you will run all day: a single review of a two-commit range was enough to exhaust a free monthly allowance during testing. The pipeline skill therefore states the cost up front and offers the single-context `dotnet-techne-code-review` skill for small diffs.

Codex also caps concurrent agent threads per session, and the default is lower than this pipeline needs, so "five parallel reviewers" otherwise run in waves of three. `scripts/install-codex.sh` sets:

```toml
[agents]
max_concurrent_threads_per_session = 6
```

If you already configure `[agents]` yourself, the installer leaves it alone and prints the setting to apply - two `[agents]` headers in one file is a TOML error.

Spawned lanes appear in the transcript as threads named after the lane (`/root/correctness`, `/root/security_observability`), with hyphens normalised to underscores; that is Codex's thread naming, not a different role.

## Limits

- **No scripted orchestration.** Codex has no workflow runtime, so the fan-out, dedupe and maintainer pass run in the orchestrator's context, and findings pass through it.
- **Model tiering** is per spawn or via `agents.default_subagent_model`; the pipeline skill states the tier a change deserves.
- **Windows**: the MCP launcher is a POSIX shell script, so run Codex under WSL2 for Synopsis; skills, roles and the guard work natively.
- The log monitor (`monitors/`) remains Claude Code only.
