# Roadmap

What is planned, so you can check before opening a request. Not a promise of dates.

## Done — 1.7.0

- OpenCode plugin: skills, `/dotnet-review`, reviewer subagents, Synopsis graph server
- Codex plugin: skills, reviewer roles, Synopsis graph server, read-only guard
- MIT licence

## Next

- **Lighter review mode** — three reviewers instead of five, for metered plans
- **Log monitor beyond Claude Code** — OpenCode and Codex both support the needed events
- **Windows check** — the OpenCode installer's PowerShell version is untested
- **Per-reviewer models in Codex** — currently one model for all spawned reviewers

## Later

- **`npm` install for OpenCode** (`opencode plugin opencode-dotnet-episteme -g`) — packaging is ready, publishing waits for 2.0
- **Reviewer roles for other tools** as their agent support settles

## Not planned

- Rewriting the skills for a single tool. They follow the [Agent Skills](https://agentskills.io/specification) standard and stay portable.
