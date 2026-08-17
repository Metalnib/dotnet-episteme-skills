# Tool compatibility

The 11 skills follow the [Agent Skills](https://agentskills.io/specification) standard, so they work in any tool that supports it. On top of that, Claude Code, Codex and OpenCode each get a plugin that adds the multi-agent pipelines (review, story QA, refactor loop — a plain-words walkthrough: [using-the-pipelines.md](using-the-pipelines.md)) and the Synopsis graph tools — see [codex-setup.md](codex-setup.md) and [opencode-setup.md](opencode-setup.md).

| Capability | Claude Code | OpenAI Codex | OpenCode | pi |
|---|---|---|---|---|
| The 11 `dotnet-techne-*` skills | plugin install | plugin install, or point `.agents/skills/` at them | plugin install, or point `.opencode/skills/` at them | add the skills folder in pi's settings |
| Review with 5 reviewers + maintainer | `/dotnet-review` | ask for it (the `dotnet-techne-review-pipeline` skill) | `/dotnet-review` | not available — one-pass review skill instead |
| Story QA vs the spec (3 lanes + maintainer) | `/dotnet-qa` | ask for it (the `dotnet-techne-qa-pipeline` skill) | `/dotnet-qa` | one-pass `dotnet-techne-story-qa` skill instead |
| Phase-gated refactor loop | `/dotnet-refactor` (+ state reload after /clear or compaction) | ask for it (the `dotnet-techne-refactor-pipeline` skill; re-read the state file yourself) | `/dotnet-refactor` (re-read the state file yourself) | not available |
| Workers cannot change files | guard hook | read-only sandbox per role | read-only permissions per subagent | not enforced |
| Synopsis graph tools | started by the plugin | started by the plugin | started by the plugin | run the CLI from the skill scripts |
| A stronger model for big changes | chosen per lane | one model for all spawned agents (per-spawn override behind an experimental flag) | opt-in `<lane>-strong` subagents | not available |
| Extra step after installing | none | one script, for the worker roles | none | copy the skills |
| Synopsis log monitor | yes (experimental) | not yet | not yet | no |
| Scripted orchestration | yes (`workflows/dotnet-review.js`, `dotnet-qa.js`, `dotnet-refactor.js`) | no — the skills drive it | no — the commands drive it | no |
| C# language server | install `csharp-lsp@claude-plugins-official` | — | built in | — |

Windows: the Synopsis graph server starts through a shell script, so run your tool under WSL2 to get it. Everything else works natively.

## Notes per tool

- **Claude Code**: everything works out of the box. Worker agents are named `dotnet-episteme-skills:<group>:<name>` (groups: `review`, `refactor`, `qa`), Synopsis tools appear as `mcp__plugin_dotnet-episteme-skills_synopsis__<tool>`. If the graph server exits it is not restarted — reconnect with `/mcp`. The refactor loop's state file (`.episteme/DESIGN-<slug>.md`) is re-injected automatically after `/clear`, compaction, or resume.
- **OpenAI Codex**: the plugin installs the skills, the graph server and the read-only guard; `scripts/install-codex.sh` adds the thirteen worker roles, because plugins are not allowed to. The pipelines are skills you ask for — Codex custom prompts exist but are deprecated in favour of skills. No reload hook: the refactor skill re-reads its state file at session start. See [codex-setup.md](codex-setup.md).
- **OpenCode**: `scripts/install-opencode.sh` registers the skills, the worker subagents, `/dotnet-review`, `/dotnet-qa`, `/dotnet-refactor` and the graph server. It also reads skills from `.claude/skills` and `.agents/skills` directly. No reload hook: the refactor command re-reads its state file at session start. See [opencode-setup.md](opencode-setup.md).
- **pi (badlogic/pi-mono)**: point pi's skills setting at the `skills/` directory. No sub-agents, so review and story QA run in one pass.

## For contributors

Keep `skills/**/SKILL.md` free of tool-specific frontmatter — they must work everywhere. Tool-specific pieces live in `agents/`, `commands/`, `.mcp.json`, `monitors/`, `bin/`, `workflows/`, `hooks/` (Claude Code), `opencode/`, and `.codex-plugin/` + `codex/` (Codex, including its own pipeline skills so the other tools don't see two entry points).

`agents/{review,refactor,qa}/*.md` stay in Claude Code's format and the other two tools convert them at install time — one copy of each worker prompt, three tools. Keep their frontmatter to `name`, `description` and a tool restriction (`tools:` allow-list, or `disallowedTools:` when the lane needs the Synopsis MCP tools visible); the converters and `scripts/validate.sh` expect one of the two.
