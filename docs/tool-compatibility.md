# Tool compatibility

The 10 skills follow the [Agent Skills](https://agentskills.io/specification) standard, so they work in any tool that supports it. On top of that, Claude Code, Codex and OpenCode each get a plugin that adds the multi-agent review and the Synopsis graph tools — see [codex-setup.md](codex-setup.md) and [opencode-setup.md](opencode-setup.md).

| Capability | Claude Code | OpenAI Codex | OpenCode | pi |
|---|---|---|---|---|
| The 10 `dotnet-techne-*` skills | plugin install | plugin install, or point `.agents/skills/` at them | plugin install, or point `.opencode/skills/` at them | add the skills folder in pi's settings |
| Review with 5 reviewers + maintainer | `/dotnet-review` | ask for it (the `dotnet-techne-review-pipeline` skill) | `/dotnet-review` | not available — one-pass review skill instead |
| Reviewers cannot change files | guard hook | read-only sandbox per role | read-only permissions per subagent | not enforced |
| Synopsis graph tools | started by the plugin | started by the plugin | started by the plugin | run the CLI from the skill scripts |
| A stronger model for big changes | chosen per reviewer | one model for all spawned reviewers (per-spawn override behind an experimental flag) | opt-in `review-*-strong` reviewers | not available |
| Extra step after installing | none | one script, for the reviewer roles | none | copy the skills |
| Synopsis log monitor | yes (experimental) | not yet | not yet | no |
| Scripted review orchestration | yes (`workflows/dotnet-review.js`) | no — the skill drives it | no — the command drives it | no |
| C# language server | install `csharp-lsp@claude-plugins-official` | — | built in | — |

Windows: the Synopsis graph server starts through a shell script, so run your tool under WSL2 to get it. Everything else works natively.

## Notes per tool

- **Claude Code**: everything works out of the box. Reviewer agents are named `dotnet-episteme-skills:review:<name>`, Synopsis tools appear as `mcp__plugin_dotnet-episteme-skills_synopsis__<tool>`. If the graph server exits it is not restarted — reconnect with `/mcp`.
- **OpenAI Codex**: the plugin installs the skills, the graph server and the read-only guard; `scripts/install-codex.sh` adds the reviewer roles, because plugins are not allowed to. The review is a skill you ask for — Codex custom prompts exist but are deprecated in favour of skills. See [codex-setup.md](codex-setup.md).
- **OpenCode**: `scripts/install-opencode.sh` registers the skills, the reviewer subagents, `/dotnet-review` and the graph server. It also reads skills from `.claude/skills` and `.agents/skills` directly. See [opencode-setup.md](opencode-setup.md).
- **pi (badlogic/pi-mono)**: point pi's skills setting at the `skills/` directory. No sub-agents, so the review runs in one pass.

## For contributors

Keep `skills/**/SKILL.md` free of tool-specific frontmatter — they must work everywhere. Tool-specific pieces live in `agents/`, `commands/`, `.mcp.json`, `monitors/`, `bin/` (Claude Code), `opencode/`, and `.codex-plugin/` + `codex/` (Codex, including its own review skill so the other tools don't see two entry points).

`agents/review/*.md` stay in Claude Code's format and the other two tools convert them at install time — one copy of each reviewer prompt, three tools. Keep their frontmatter to `name`, `description` and a tool restriction; `tools:` is the strictest option and is what the converters expect.
