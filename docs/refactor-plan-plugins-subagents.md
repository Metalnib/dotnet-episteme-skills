# Refactor plan: Claude Code plugin subagents + Synopsis MCP (v1.6.0)

Status: in progress on branch `refactor/plugin-subagents-v2`.

## Why

The repo is already a Claude Code plugin (skills only). This refactor adds the Claude-specific layers that plain skills cannot provide, without giving up portability:

1. **Multi-subagent code review.** The code-review skill runs today as one long single-context procedure. Focused reviewers in fresh, isolated contexts find more (no context pollution between domains), and an adversarial "maintainer" agent that tries to refute findings kills false positives before they reach the user. Anthropic's own code-review plugin (4 parallel agents) and security-review (false-positive filter pass) validate this shape.
2. **Synopsis as a first-class MCP server.** `synopsis mcp` already speaks MCP over stdio; the plugin just never declared it. Wiring it in gives every session direct access to the 17 graph tools (blast_radius, breaking_diff, db_lineage, ...) with persistent state (no rescan per session).
3. **Log visibility.** Synopsis gains `--log-file`; an experimental plugin monitor tails it so daemon diagnostics reach the session.

## Portability rule (the prime directive)

`skills/` folders stay the portable single source of truth, per the Agent Skills standard (agentskills.io):

- **OpenCode** reads Claude-format SKILL.md folders natively (also from `.claude/skills/`).
- **pi** (badlogic/pi-mono) consumes Claude skill dirs via settings paths.
- **OpenAI Codex** uses the same SKILL.md format via `.agents/skills/` or `skills.config` in config.toml.

None of these can run the Claude multi-agent pipeline (Codex subagents are separate TOML files with `max_depth` 1 by default; OpenCode agents are its own markdown format; pi has no delegation primitive). Therefore:

- No Claude-only frontmatter keys are added to any SKILL.md.
- SKILL.md remains the complete single-context review procedure; the new maintainer playbook is a portable reference file used by both paths.
- Everything Claude-specific lives in additive plugin layers other tools ignore: `agents/`, `commands/`, `.mcp.json`, `monitors/`, `bin/`.

See `docs/tool-compatibility.md` for the feature × tool matrix.

## Architecture: the review pipeline

```
/dotnet-episteme-skills:dotnet-review <target> [--cynical]
        |
  [main thread]
  resolve target + mode, run context scripts once
  (list-changes, branch-diff, review-context)
        |
        +--parallel--> review:correctness             (correctness + API design + style)
        +--parallel--> review:performance             (perf, low-GC, AOT/trimming)
        +--parallel--> review:security-observability  (security + logging/observability)
        +--parallel--> review:data-messaging          (EF Core/PostgreSQL + RabbitMQ
        |                                              + HTTP integration; Synopsis MCP)
        +--parallel--> review:generalist              (no lane: tests, config/build,
        |                                              requirements, cross-cutting)
  collect findings --> dedupe/merge (same Location+Area: merge, keep highest severity)
        |
        +-----------> review:maintainer  (fresh ctx: findings + diff + maintainer-playbook.md)
        |               verdicts: CONFIRMED / DOWNGRADED(severity) / REFUTED(file:line evidence)
        |
  apply verdicts, format per references/output-contract.md
```

Decisions and rationale:

- **4 grouped domain reviewers, not 7 fine-grained.** Paired domains inspect the same code paths (security and logging both walk request handling; EF and RabbitMQ share transaction/outbox concerns). Grouping keeps signal while halving latency/token cost. (Implementation added a 5th, `review:generalist`, with no assigned lane - it hunts what falls between the domains: test quality, config/build, requirements mismatch, cross-cutting design.)
- **One counter-thesis maintainer, not per-finding verifiers.** Typical reviews yield 5-20 findings; a single fresh-context maintainer sees cross-finding context (dedupes, spots contradictions) at 1 launch instead of N. Per-finding parallel verification is a future option if reviews regularly exceed ~25 findings.
- **Refutation requires evidence.** The maintainer may only REFUTE with concrete `file:line` evidence (existing guard, test, invariant, design intent) - never plausibility. Rules live in `skills/dotnet-techne-code-review/references/maintainer-playbook.md` (portable; single-context mode uses it in the falsification step).
- **Agents don't duplicate checklist prose.** The orchestrator command passes absolute paths to `domain-checklists.md` plus assigned sections in each delegation prompt.
- **A real bundled workflow script, Task calls as fallback.** The plugin ships `workflows/dotnet-review.js` (scout sizes the change → model tier picked in code → 5 reviewers in parallel via `agentType` → dedupe in code → maintainer verify with per-finding escalation above 10 blocking findings). The command invokes it with `Workflow({scriptPath: ${CLAUDE_PLUGIN_ROOT}/workflows/dotnet-review.js, args})` - validated empirically (scriptPath accepts plugin paths; args arrive as a JSON string, so the script parses defensively). Workflows are not a plugin *component* (they live in `.claude/workflows/`), so `scripts/install-workflow.sh`/`.ps1` optionally copies the script there for a native `/dotnet-review` command; the copy must be re-run after plugin updates, the scriptPath route always matches the installed plugin version. Task-call orchestration remains as the fallback where workflows are unavailable, and document/spec review stays on the skill path.

## Synopsis wiring

- **MCP**: `.mcp.json` at plugin root declares a `synopsis` server running `bin/synopsis-mcp-launcher.sh`, which resolves the platform binary via the existing `detect-tool.sh` (PATH -> skill bin -> dev artifacts -> GitHub release download) and `exec`s `synopsis mcp --root "$PWD" --state-dir "$DATA/state" --log-file "$DATA/synopsis.log"` with `DATA="${CLAUDE_PLUGIN_DATA:-$HOME/.synopsis}"`. Tools appear as `mcp__plugin_dotnet-episteme-skills_synopsis__*`. Windows: run Claude Code under WSL2 (the launcher is a POSIX shell script; Synopsis then runs as a Linux binary); native Windows can still drive Synopsis via the CLI path through `detect-tool.ps1`.
- **`--log-file` (new in Synopsis 1.6.0)**: MCP diagnostics go to stderr as before AND append to the log file when configured (`--log-file` flag or `SYNOPSIS_LOG_FILE` env). Older binaries ignore the flag harmlessly.
- **Monitor (experimental)**: `monitors/monitors.json` tails the log, starting on first invocation of the synopsis skill. Interactive CLI sessions only; best-effort, never a functional dependency.
- **LSP: evaluated, not applicable.** Synopsis speaks MCP JSON-RPC, not the Language Server Protocol; plugin `.lsp.json` only configures real LSP binaries. An LSP facade over a graph-query tool would add nothing over the MCP tools.
- MCP servers are not auto-respawned if they exit; reconnect via `/mcp`.

## Phases

- **Phase 0** - branch, this document, `docs/tool-compatibility.md`.
- **Phase 1** - `references/maintainer-playbook.md`; six agents under `agents/review/` (correctness, performance, security-observability, data-messaging, generalist, maintainer); `commands/dotnet-review.md` orchestrator; additive SKILL.md edits (playbook wired into the falsification step; pointer to the command on Claude Code).
- **Phase 2** - Synopsis `--log-file` (C# + tests); `bin/synopsis-mcp-launcher.sh`; `.mcp.json`; synopsis SKILL.md updates (prefer MCP tools when visible).
- **Phase 3** - `monitors/monitors.json`.
- **Phase 4** - packaging: plugin.json + marketplace.json 1.6.0 (drop the empty `"agents": []` key - a present key *replaces* the default `agents/` scan, so an empty array would mask the directory); `scripts/validate.sh` extended to agents/commands/mcp/monitors; `Directory.Build.props` 1.6.0; CHANGELOG; README per-tool matrix.
- **Phase 5** - verification: validate scripts, `dotnet test`, local-marketplace install, end-to-end `/dotnet-review` run, portability regression (`git diff main -- 'skills/**/SKILL.md'` shows no new frontmatter keys).

## Versioning

Everything moves to **1.6.0**: plugin.json, marketplace.json, CHANGELOG, `src/synopsis/Directory.Build.props`, skill metadata versions. This is a minor bump over the 1.5.0/1.5.1 line (1.5.1 was tagged without a CHANGELOG entry or binary bump) and makes the cross-repo-impact skill's "Requires Synopsis v1.6.0+" requirement true. Tag `v1.6.0` after merge; release CI rebuilds the 6 RID binaries.

## Follow-ups (out of scope)

- PowerShell MCP launcher for Windows auto-start.
- Per-finding parallel verification mode for very large reviews.
- `model`/`effort` hints on reviewer agent frontmatter once real usage data exists.
- Mirroring the review pipeline as Codex TOML subagents (`.codex/agents/`).
