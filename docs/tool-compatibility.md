# Tool compatibility

The `skills/` folders follow the [Agent Skills](https://agentskills.io/specification) standard and work in any client that implements it. Beyond that, three integration layers ship here: a Claude Code plugin (`.claude-plugin/`, `agents/`, `commands/`, `.mcp.json`, `hooks/`, `monitors/`, `workflows/`), an OpenCode plugin (`opencode/` — see [opencode-setup.md](opencode-setup.md)), and a Codex plugin (`.codex-plugin/`, `codex/` — see [codex-setup.md](codex-setup.md)). Each tool ignores the others' layers; `skills/`, `agents/review/`, `bin/` and `hooks/` are shared.

| Capability | Claude Code | OpenAI Codex | OpenCode | pi |
|---|---|---|---|---|
| The 10 `dotnet-techne-*` skills | plugin install (marketplace) | plugin install — `.codex-plugin/plugin.json` + this repo's `marketplace.json` (verified on codex-cli 0.145.0); or copy/point `.agents/skills/`, `skills.config` in config.toml | plugin install (`opencode plugin opencode-dotnet-episteme -g` or `scripts/install-opencode.sh`); also reads `.claude/skills/`, `.agents/skills/`, `.opencode/skills/` | add skills dir to pi settings |
| Multi-subagent review (5 reviewers + maintainer) | yes, `/dotnet-review` (plugin `agents/` + `commands/`) | yes — six `review-*` roles + the `dotnet-techne-review-pipeline` skill (`scripts/install-codex.sh`) | yes — plugin registers six `review-*` subagents + `/dotnet-review` | no (single-context SKILL.md path) |
| Maintainer pushback (counter-thesis pass) | dedicated fresh-context agent | dedicated role (`review-maintainer`) | dedicated fresh-context subagent (`review-maintainer`) | in-context falsification step (maintainer-playbook.md) |
| Read-only reviewer sandbox | `hooks/git-readonly-guard.sh` (PreToolUse) | per-role `sandbox_mode = "read-only"` (the guard runs but cannot scope to a role) | per-agent `permission` rules registered by the plugin (last-match-wins, chained commands parsed) | not enforced |
| Synopsis graph tools | MCP server auto-start (plugin `.mcp.json`; macOS/Linux, Windows via WSL2) | MCP server auto-start via `.codex-plugin` → `codex/mcp.json` (macOS/Linux, Windows via WSL2) | MCP server auto-registered by the plugin (macOS/Linux, Windows via WSL2) | CLI via skill scripts |
| Per-reviewer model tier | per-invocation `model` on each Task call | per spawn, or `agents.default_subagent_model` | no per-call model on `task`; opt-in `review-*-strong` variants pinned via `DOTNET_EPISTEME_STRONG_MODEL` | no |
| Synopsis log monitor | experimental plugin monitor (interactive CLI only) | no | no (port target: `event` / `session.*` plugin hooks) | no |
| Scripted multi-agent orchestration | bundled `workflows/dotnet-review.js`, run via `scriptPath` by the command (Task-call fallback); `scripts/install-workflow.sh` copies it to `.claude/workflows/` for a native `/dotnet-review` | no workflow runtime; the pipeline skill drives the fan-out, so merge/dedupe happen in the orchestrator's context | no workflow engine; the command drives parallel `task` calls, so merge/dedupe happen in the orchestrator's context | no |
| LSP | n/a for Synopsis (speaks MCP); for C# intelligence install `csharp-lsp@claude-plugins-official` | n/a | built-in LSP | n/a |

## Notes per tool

- **Claude Code**: everything works out of the box. Reviewer agents are named `dotnet-episteme-skills:review:<name>`, Synopsis tools appear as `mcp__plugin_dotnet-episteme-skills_synopsis__<tool>`. If the graph server exits it is not restarted — reconnect with `/mcp`.
- **OpenAI Codex**: the plugin installs the skills, the graph server and the read-only guard; `scripts/install-codex.sh` adds the reviewer roles, because plugins are not allowed to. Codex has no custom commands, so the review is a skill you ask for. See [codex-setup.md](codex-setup.md).
- **OpenCode**: `scripts/install-opencode.sh` registers the skills, the reviewer subagents, `/dotnet-review` and the graph server. It also reads skills from `.claude/skills` and `.agents/skills` directly. See [opencode-setup.md](opencode-setup.md).
- **pi (badlogic/pi-mono)**: point pi's skills setting at the `skills/` directory. No sub-agents, so the review runs in one pass.

## For contributors

Keep `skills/**/SKILL.md` free of tool-specific frontmatter — they must work everywhere. Tool-specific pieces live in `agents/`, `commands/`, `.mcp.json`, `monitors/`, `bin/` (Claude Code), `opencode/`, and `.codex-plugin/` + `codex/` (Codex, including its own review skill so the other tools don't see two entry points).

`agents/review/*.md` stay in Claude Code's format and the other two tools convert them at install time — one copy of each reviewer prompt, three tools. Keep their frontmatter to `name`, `description` and a tool restriction; `tools:` is the strictest option and is what the converters expect.

Docs are for users: what this is, how to install it, how to use it. Planned work goes in [roadmap.md](roadmap.md), and design history belongs in commit messages, not in the docs or in code comments.
