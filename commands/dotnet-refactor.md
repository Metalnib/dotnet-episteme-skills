---
description: Phase-gated refactoring/design loop - session-blind subagents build the map and traces, empirical probes precede the design, an approval gate guards implementation, and a conformance audit re-checks the whole branch after any design change. State persists in .episteme/DESIGN-<slug>.md so the loop survives compaction and fresh sessions.
argument-hint: "<ticket|area> [--design-file <path>] [--lite] e.g. YB-1234 or 'error handling in OrderService'"
---

# /dotnet-refactor - orchestrated design loop

You are the orchestrator. Do not enumerate or trace code yourself - dispatch session-blind
workers, persist their artifacts, and hold the gates. **No code is written or edited until the
design is approved at the Phase D gate.**

Arguments given: `$ARGUMENTS`

## Step 0 - Resolve target and state

- Target: a ticket id or an area description. Slug it (e.g. `yb-1234` or `orderservice-errors`).
- State file: `--design-file` if given, else `.episteme/DESIGN-<slug>.md` at the repository root
  (create `.episteme/` if missing; whether the host repo gitignores it is the user's call).
- **If the file exists**, route on its frontmatter - the file is the source of truth, never
  conversation memory. This is the context-compaction contract: after `/clear`, compaction, or a
  fresh session, the loop continues losslessly from the file (a plugin hook re-injects it).

  | frontmatter | resume at |
  |---|---|
  | `status: done` | nothing to resume - say so and stop |
  | `status: blocked` | restate the recorded blocker, ask the user |
  | `status: awaiting-approval` | re-present the design, hold the Phase D gate |
  | `status: active` | the phase in `phase:` |

- **If not**, create it:

  ```markdown
  ---
  target: <ticket id or area, one line>
  phase: R
  status: active
  design_revision: 0
  ---
  ## Map
  ## Traces
  ## Probes
  ## Design (approved)
  ## Decision log
  ## Deferred
  ## Todo
  ## Review findings
  ```

  Keep the frontmatter current: advance `phase` (R, E, T, P, D, I, V, F) the moment a phase
  completes, and flush worker artifacts to the file the moment they return - unpersisted state
  does not survive compaction. `Decision log` and `Deferred` are append-only.
- Invariant pack: if the host repository has project-specific design invariants (a local
  `/redesign`-style command, a CLAUDE.md section, or architecture docs), extract the 5-15 lines
  relevant to the target. Workers receive this pack and artifact data only - never conversation
  narration; the state file is the sole hand-off medium.

## Step 1 - Phase R: recall

State to the user, in 3-5 bullets, the standing rules that apply plus anything in the state
file's Decision log. The standing rules:

- Fix the **invariant, not the instance**: a found defect names a rule; sweep for every
  violation of the rule before fixing anything.
- **Dataflow over references**: "who uses this type" is not "what value reaches this branch
  during an outage / on duplicates / on empty input".
- **Distrust names after changing behavior**: doc labels, class names, log texts, annotations,
  guard-looking code - re-read every artifact on a changed path.
- **Empirical before architectural**: never design on assumed external/vendor behavior.

## Step 2 - Phases E+T: enumerate and trace

Mode first: **lighter mode** for a small target - one service, a handful of paths (`--lite`, or
your own judgment; when unsure run the full pipeline). One `refactor:surveyor` worker maps and
traces in a single pass, and reports `ESCALATE: <reason>` when the area turns out bigger than
one pass should carry - then rerun in full mode.

Preferred - the bundled workflow (this command is your opt-in):

```
Workflow({
  scriptPath: "${CLAUDE_PLUGIN_ROOT}/workflows/dotnet-refactor.js",
  args: {
    phase: "map",
    lite: <true for lighter mode>,
    target: "<target>",
    repoRoot: "<absolute repo root>",
    services: ["<service dir>", ...],   // full mode; omit for single-service targets
    invariantPack: "<pack or omit>",
    scopeHints: "<hints or omit>"
  }
})
```

Full mode fans out one cartographer per service, then pipelines one tracer per path group the
cartographers propose; lighter mode runs the single surveyor. Raw worker output never enters
this conversation - you receive `{map, traces, anomalies, uncertainties}` (plus
`escalate` when the surveyor bailed out; rerun with `lite: false`).

Fallback (Workflow tool unavailable): lighter mode is one
`dotnet-episteme-skills:refactor:surveyor` Task call; full mode launches
`dotnet-episteme-skills:refactor:cartographer` Task calls - one per service, all **in one
message** - with repository root, target, invariant pack, and scope hints, then
`dotnet-episteme-skills:refactor:tracer` per path group with the map rows in scope.

Either way: write the map into `Map` and the hop-chain tables into `Traces` verbatim, advance
`phase`, and show the user the map and the anomaly list. The map is the scope: everything later
phases touch must appear here, and a touchpoint discovered later means the map was wrong -
update the map first, then continue.

## Step 3 - Phase P: probe

Where the design depends on external/vendor behavior (error shapes, fault texts, empty-vs-fault
semantics, timeouts), verify it empirically **before** designing - in the main loop, since
probes may need credentials, VPN, or sandbox access the workers must not have. Integration-test
probes are the pattern; a path that cannot be produced gets a ready-to-run skipped test, never
an assumption. Persist results in `Probes`. A probe result is allowed to change the design -
that is its purpose.

## Step 4 - Phase D: design gate (STOP)

Write the design in prose: the invariant being established, type/structure changes, the full
touchpoint list from the map with what changes at each, alternatives with a recommendation.
Set `status: awaiting-approval`, present the design, and stop with exactly these options:

1. **Approve and continue** - proceed to Phase I now.
2. **Approve and stop** - record everything, end here; any fresh session resumes at Phase I
   from the file alone.
3. **Revise** - apply the requested changes and present again.

On approval (either flavor): record the design under `Design (approved)` inside a
`<frozen-after-approval>` fence with the date and the user's choice, log the decision, derive
`Todo` items - one per touchpoint - and set `phase: I`, `status: active` (or `status: blocked`
with a note if the user stopped without approving). Everything inside the fence is locked:
later phases treat it read-only, and only an explicit user decision may change it.

## Step 5 - Phase I: implement

- Work through `Todo`, checking items off in the state file as they land. Apply the design to
  **every** touchpoint, not only the ones the ticket named.
- Scope discipline: defects or improvements outside the approved design go to `Deferred`
  (append-only: `location | finding | why deferred`), never fixed inline.
- **If the design must change mid-flight**: stop; get the user's explicit approval for the
  change; update the fenced design, bump `design_revision`, log the decision. Then run the
  conformance audit on the full branch diff - preferred via the workflow
  (`args: { phase: "audit", repoRoot, designFile: "<state file path>" }`), fallback a
  `dotnet-episteme-skills:refactor:conformance-auditor` Task call with the approved design and
  the diff. Fix every nonconformance it reports before moving forward: a design change
  invalidates all prior work in the branch until this audit passes. Judge the branch by its
  diff, never by your memory of what you wrote.
- Audit cap: after 3 audit rounds within one loop, halt and escalate to the user - the design
  is not converging.

## Step 6 - Phase V: verify

- Tests assert at the boundary the task is about (serialized payloads, wire status, log text) -
  never only `BeOfType<SomeResult>` when the ticket is about what is in the body.
- Prove a regression test fails when the fix is removed; do not assume it.
- Run integration tests that are not in CI (adapter/sandbox suites) when the change touches them.
- Then run `/dotnet-review` on the working diff and process its findings; record the outcome in
  `Review findings`.

## Step 7 - Phase F: final mechanical pass

Re-read the full diff hunk by hunk against the host repo's rules: comment style, commit-message
style, stale XML crefs / doc labels / log texts, unused usings, docs updated where behavior
changed. Run the IDE linter on all touched files if available (it catches stale crefs the
compiler does not). Set `status: done` in the state file.

## Worker restrictions

Workers follow the same contract as the review agents (`docs/reviewer-restrictions.md`): no
writes, no web, no nested agents, and no conversation history in their prompts - their
blindness to the session is the anti-anchoring mechanism, not an accident. The plugin's
PreToolUse hook enforces a read-only shell: git and synopsis everywhere, plus the read-only
search/list tools (rg/fd with grep/find fallbacks, ls, cat, head, tail, wc, tree) for the
refactor lanes, which sweep whole solutions.
