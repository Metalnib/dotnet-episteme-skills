# Roadmap

What is planned, so you can check before opening a request. Not a promise of dates.

## Done — 1.7.0

- OpenCode plugin: skills, `/dotnet-review`, reviewer subagents, Synopsis graph server
- Codex plugin: skills, reviewer roles, Synopsis graph server, read-only guard
- MIT licence

## Next

- **OpenCode v2 plugin API** — a dormant v2 module ships (`scripts/install-opencode.sh --v2`); finalize when the beta API stabilises and expose Synopsis MCP through it
- **Lighter review mode** — three reviewers instead of five, for metered plans
- **Log monitor beyond Claude Code** — OpenCode and Codex both support the needed events
- **Windows check** — the OpenCode installer's PowerShell version is untested
- **Per-reviewer models in Codex** — the spawn tool's `model` override sits behind the experimental `features.multi_agent_v2.expose_spawn_agent_model_overrides` flag; adopt when it stabilises

## Later

- **`npm` install for OpenCode** (`opencode plugin opencode-dotnet-episteme -g`) — packaging is ready, publishing waits for 2.0
- **Reviewer roles for other tools** as their agent support settles

## Not planned

- Rewriting the skills for a single tool. They follow the [Agent Skills](https://agentskills.io/specification) standard and stay portable.
