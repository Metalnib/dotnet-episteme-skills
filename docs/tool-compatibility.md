# Tool compatibility

The `skills/` folders follow the [Agent Skills](https://agentskills.io/specification) standard and work in any client that implements it. Everything else in this repo is a Claude Code plugin layer that other tools ignore.

| Capability | Claude Code | OpenAI Codex | OpenCode | pi |
|---|---|---|---|---|
| The 10 `dotnet-techne-*` skills | plugin install (marketplace) | copy/point `.agents/skills/` or `skills.config` in config.toml | native (`.opencode/skills/`, also reads `.claude/skills/`) | add skills dir to pi settings |
| Multi-subagent review (`/dotnet-review`, 5 reviewers + maintainer) | yes (plugin `agents/` + `commands/`) | no (single-context SKILL.md path) | no (single-context SKILL.md path) | no (single-context SKILL.md path) |
| Maintainer pushback (counter-thesis pass) | dedicated fresh-context agent | in-context falsification step (maintainer-playbook.md) | same as Codex | same as Codex |
| Synopsis graph tools | MCP server auto-start (plugin `.mcp.json`; macOS/Linux, Windows via WSL2) | manual: `synopsis mcp` as MCP server in config.toml `mcp_servers` | manual: MCP config, or CLI via skill scripts | CLI via skill scripts |
| Synopsis log monitor | experimental plugin monitor (interactive CLI only) | no | no | no |
| Scripted multi-agent orchestration | bundled `workflows/dotnet-review.js`, run via `scriptPath` by the command (Task-call fallback); `scripts/install-workflow.sh` copies it to `.claude/workflows/` for a native `/dotnet-review` | no workflow runtime; nearest: `spawn_agents_on_csv` batch tool, `codex exec --json` shell pipelines, cloud `--attempts` best-of-N | no workflow engine; DIY via server/SDK (child sessions, async prompts, per-call `agent`+`model`) or community plugins | no |
| LSP | n/a for Synopsis (speaks MCP); for C# intelligence install `csharp-lsp@claude-plugins-official` | n/a | built-in LSP | n/a |

## Notes per tool

- **Claude Code**: full feature set. Agents are namespaced `dotnet-episteme-skills:review:<name>`; Synopsis MCP tools appear as `mcp__plugin_dotnet-episteme-skills_synopsis__<tool>`. MCP servers are not auto-respawned after a crash — reconnect with `/mcp`.
- **OpenAI Codex**: skills use the same SKILL.md format; scanned from `.agents/skills` (repo root/CWD/parents), `~/.agents/skills`, `/etc/codex/skills` — not `.codex/skills`. Codex has its own subagent mechanism (TOML files in `.codex/agents/`, `max_depth` 1 by default) — mirroring the review pipeline there is a possible follow-up, not maintained here. Per-spawn model override exists in the runtime but is hidden from the model by default (`hide_spawn_agent_metadata`), so the agent TOML `model` field is the reliable path. Synopsis can be registered manually: `mcp_servers.synopsis` with `command = "synopsis"`, `args = ["mcp", "--root", ".", "--state-dir", "~/.synopsis/state"]`.
- **OpenCode**: consumes these skill folders without conversion from its native dirs, `.claude/skills`, and `.agents/skills` (frontmatter `name`/`description` required; `license`/`compatibility`/`metadata` are within the standard). Its own agents/plugins are a different format; the single-context SKILL.md path is the supported one. If mirroring the review pipeline: OpenCode's task tool has no per-invocation model override — use command frontmatter (`model:` + `agent:` + `subtask: true`) to force a subagent on a chosen model.
- **pi (badlogic/pi-mono)**: point pi's skills setting at the `skills/` directory. No subagent/delegation primitive; single-context path only.

## The rule for contributors

Never add Claude-only frontmatter keys to any `skills/**/SKILL.md`. Claude-specific behavior belongs in `agents/`, `commands/`, `.mcp.json`, `monitors/`, or `bin/`.
