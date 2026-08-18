# Roadmap

What is planned, so you can check before opening a request. Not a promise of dates.

## Done — 1.8.0

- Refactor pipeline: `/dotnet-refactor` phase-gated design loop with session-blind map/trace workers, a design approval gate, a conformance audit after design changes, `.episteme/DESIGN-<slug>.md` state that survives compaction (reload hook on Claude Code), and a `--lite` single-worker mode for small targets
- Story QA pipeline: `/dotnet-qa` verifies a story against its spec — per-AC verdicts, reuse/design conformance, dead code — with a spec-discovery cascade and a deterministic FAIL/CONCERNS/PASS gate persisted to `.episteme/QA-<slug>.md`
- Both pipelines on all three tools (commands on Claude Code/OpenCode, pipeline skills on Codex) with the worker-restriction contract extended to the new lanes

## Done — 1.7.0

- OpenCode plugin: skills, `/dotnet-review`, reviewer subagents, Synopsis graph server
- Codex plugin: skills, reviewer roles, Synopsis graph server, read-only guard
- One reviewer-restriction contract across all three tools ([reviewer-restrictions.md](reviewer-restrictions.md)): no writes, read-only git, no web, no nested agents
- MIT licence

## Next

- **Synopsis warm daemon** — keep the workspace hot inside `synopsis mcp`: a file watcher plus incremental re-analysis, so queries reflect an edit in seconds instead of waiting on a full rescan. Staleness metadata (`lastIndexed`, `pendingChanges`) on every reply so clients can show how fresh an answer is. Benchmark on a large solution first — the warm compilation trades memory for latency, and the numbers decide.
- **Shift-left breaking-diff** — an opt-in pre-push / stop hook that runs `synopsis breaking-diff` on the working diff, so a breaking change surfaces at commit time instead of in PR review.
- **Blast radius on edit (Claude Code)** — after an edit to a `.cs` file, a hook asks the warm daemon for the downstream impact and injects it as context. Hooks never index; the daemon does.
- **OpenCode v2 plugin API** — a dormant v2 module ships (`scripts/install-opencode.sh --v2`); finalize when the beta API stabilises and expose Synopsis MCP through it
- **Lighter review mode** — three reviewers instead of five, for metered plans
- **Log monitor beyond Claude Code** — OpenCode and Codex both support the needed events
- **Windows check** — the OpenCode installer's PowerShell version is untested
- **Per-reviewer models in Codex** — the spawn tool's `model` override sits behind the experimental `features.multi_agent_v2.expose_spawn_agent_model_overrides` flag; adopt when it stabilises

## Later

- **One daemon, many clients** — a config recipe for pointing local sessions at a shared `synopsis mcp --socket`/`--tcp` daemon, so every tool on a machine (or a whole team) queries one always-fresh graph
- **[Aegis](https://github.com/Metalnib/aegis) integration** — the autonomous fleet-review bot built on Synopsis. It already consumes the daemon transports and the versioned result envelope (`schemaVersion`); next is adopting the portable maintainer playbook and reviewer lanes, so the bot and the in-editor review apply the same standards
- **`npm` install for OpenCode** (`opencode plugin opencode-dotnet-episteme -g`) — packaging is ready, publishing waits for 2.0
- **Reviewer roles for other tools** as their agent support settles

## Not planned

- Rewriting the skills for a single tool. They follow the [Agent Skills](https://agentskills.io/specification) standard and stay portable.
