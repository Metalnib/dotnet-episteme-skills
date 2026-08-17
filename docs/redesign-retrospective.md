# Refactor pipeline: retrospective and plan

Status: shipped in 1.8.0. This file stays as the design record; the open questions below are
resolved in the Decision log.

## Why

YB-13323 (agent-1-mono) took four review rounds for what the final architecture shows was one
round of work. The retrospective identified the failure modes; this pipeline mechanizes their
prevention. The failure modes:

1. **Instance-fixing.** Each review finding was fixed where it was pointed at; the next round
   found another instance of the same class. The fix for a found defect is the invariant it
   violates, swept across the whole area, not the flagged line.
2. **References instead of dataflow.** "Who uses this type" was checked; "what value reaches
   this branch during an outage" was not. A dead `!IsFaulted` guard sat inside a rewritten
   method and survived three rounds. The highest-consequence bug of the branch (duplicate
   membership creation during partial outage) lived there.
3. **Design before enumeration.** "Start from scratch, rethink the design" produced code within
   minutes, with only the ticket-named path recon'd. The full branch map (one grep, 66 branches)
   was built two rounds later, under protest. The one phase done map-first survived review.
4. **Assumed vendor behavior.** The findBySSN empty-vs-fault semantics were designed on
   assumption; the 12-second sandbox probe, when finally run, changed the design (any fault is
   abnormal - stronger and simpler than the transport-only handling that was planned).
5. **Context as noise.** Four rounds of stale designs, failed attempts, and 700-line file reads
   sat in the conversation with the same standing as decisions. Keeping the context was a bet
   that the mistakes would teach; instead they anchored. There was also no artifact to restart
   from, so a fresh session was never safe.

## Architecture

Mirror of the dotnet-review pipeline: an orchestrator command holds the interactive gates,
session-blind workers do the read-heavy phases in isolated contexts, and only structured
artifacts return to the conversation. Two additions the review pipeline does not need:

- **A durable state file** (`DESIGN-<slug>.md` in the host repo). The loop is multi-round;
  every phase persists its artifact (map, traces, probe results, approved design, decision log,
  todo). `/clear` or a fresh session resumes from the file. This is the context-compaction
  contract: conversation history is never load-bearing.
- **A conformance audit after any mid-flight design change.** A design change invalidates all
  prior branch work; a blind worker gets only the design doc and the diff and reports every
  nonconformance and every surviving instance of the eliminated defect classes. The author does
  not grade their own homework.

Phases: R recall, E enumerate (cartographer), T trace (tracer), P probe (main loop - needs
sandbox/VPN/credentials workers must not have), D design gate (STOP for approval), I implement,
V verify (boundary assertions, then reuse /dotnet-review), F mechanical final pass.

Division of knowledge: the pipeline is generic; project invariants stay in the host repo
(agent-1-mono keeps its local /redesign with the error-taxonomy rules and sandbox probe
patterns) and are passed to workers as an "invariant pack" - same idea as the review command's
intent pack.

## What exists (this branch of work)

- `commands/dotnet-refactor.md` - the orchestrator.
- `agents/refactor/cartographer.md` - branch/consumer/sibling/words map, session-blind.
- `agents/refactor/tracer.md` - hop-chain dataflow, hunts dead guards, conflations,
  name-vs-behavior mismatches, wrong blame direction.
- `agents/refactor/conformance-auditor.md` - post-design-change audit of the whole branch.
- Workers reuse the reviewer restriction contract (no writes, read-only git, no web, no nested
  agents) via `disallowedTools`, following the data-messaging agent's pattern so Synopsis MCP
  tools stay reachable.

## Open questions / deferred

- **Workflow script** (`workflows/dotnet-refactor.js`): deterministic fan-out for E/T with
  intermediate output never entering the conversation, like dotnet-review.js. Task-call
  orchestration first; script when the shape has survived a real task.
- **Synopsis in workers**: cartographer mentions blast_radius/find_paths opportunistically.
  Decide whether the command should verify daemon availability up front and degrade explicitly.
- **Portable SKILL.md variant**: per the portability prime directive, a single-context version
  of the loop for OpenCode/Codex/pi. The phase procedure ports; the blind-worker isolation does
  not. Decide whether a degraded single-context version is worth shipping.
- **State-file convention**: `DESIGN-<slug>.md` at repo root vs a `docs/design/` folder;
  interaction with agent-1-mono's existing /continue (PLAN files) - one convention would be
  better than two.
- **Naming**: /dotnet-refactor vs /dotnet-redesign. Refactor chosen for symmetry with
  /dotnet-review; revisit before release.
- **Lighter mode**: single cartographer+tracer combined worker for small targets, mirroring the
  planned lighter review mode.
- **Guard reuse**: hook the git-readonly-guard for the refactor workers the way review does.

## Context compaction automation (verified against the Claude Code docs)

The state-file save/reload does not need to be manual. Three hook events cover it
(code.claude.com/docs/en/hooks.md):

- **PreCompact** - fires before manual (`matcher: manual`) and automatic (`matcher: auto`)
  compaction. Can inject `additionalContext` and can even block (exit 2). Use: a `prompt`-type
  hook telling the model to flush pending map/trace/decision updates to the DESIGN file before
  the summary is produced - so auto-compact never destroys unflushed loop state.
- **SessionStart with `compact`/`clear`/`resume` matchers** - fires right after compaction (or
  /clear, or resume). Can inject `hookSpecificOutput.additionalContext`. Use: re-inject the
  active DESIGN file automatically - the loop resumes with zero user action. Implemented as
  `hooks/design-state-reload.sh` (finds newest `DESIGN-*.md` without `Status: done`, injects up
  to 16 KB plus a resume instruction).
- **PostCompact** exists for logging/cleanup if needed.

Wiring to add to `hooks/hooks.json` when this ships:

```json
{
  "SessionStart": [
    {
      "matcher": "compact|clear|resume",
      "hooks": [{ "type": "command", "command": "${CLAUDE_PLUGIN_ROOT}/hooks/design-state-reload.sh", "timeout": 10 }]
    }
  ],
  "PreCompact": [
    {
      "matcher": "auto",
      "hooks": [{ "type": "prompt", "prompt": "If a /dotnet-refactor loop is active, write any unpersisted map/trace/decision state to its DESIGN file before compaction." }]
    }
  ]
}
```

Open points: confirm the `matcher` regex syntax for multi-source SessionStart (may need three
entries); confirm `prompt`-type hook shape for PreCompact; size cap for additionalContext is
undocumented, hence the 16 KB head. Manual lever also exists: `/compact <instructions>` accepts
focus instructions, so the command can suggest `/compact keep the approved design and open todo`
at phase gates - but with the two hooks above even unprompted auto-compact is lossless.

## Decision log

- 2026-07-29: pipeline drafted from the YB-13323 retrospective (Claude, session with Hristo).
  Repo-local /redesign kept in agent-1-mono for project knowledge; generic loop lives here.
- 2026-08-17 (1.8.0): open questions resolved. Workflow script shipped
  (`workflows/dotnet-refactor.js`, map + audit phases). State files moved off the repo root
  into `.episteme/` (`DESIGN-<slug>.md`, and the QA pipeline's `QA-<slug>.md`) with
  status-routed frontmatter; the reload hook greps `status: done` there - validate.sh guards
  the convention against drift. Naming stays `/dotnet-refactor`. Guard, validate.sh and
  test-guard.sh extended to the refactor (and new qa) lanes. Hooks wired: four SessionStart
  entries (startup/compact/clear/resume - separate entries instead of an alternation matcher,
  which needs Claude Code >=2.1.191) plus PreCompact prompt hooks for both `auto` and `manual`
  compaction; both shapes verified against current docs. Full OpenCode/Codex parity shipped (command template / pipeline skill;
  no reload hook there - the loop re-reads the state file at session start). Lighter
  single-worker mode shipped too (`refactor:surveyor`, `--lite`, honest ESCALATE when the area
  outgrows one pass), and the refactor lanes' shell widened to the read-only search/list tools
  (fast tools with GNU fallbacks) per Hristo's direction not to over-restrict them.
  BMAD mechanisms adopted after research: status-routed
  resume, `<frozen-after-approval>` fence, enumerated approval menu, deferred ledger,
  judge-the-diff, audit-loop cap; render-to-snapshot and config-layer machinery deliberately
  skipped.
