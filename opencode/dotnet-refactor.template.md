---
description: "Phase-gated refactoring/design loop - session-blind subagents build the map and traces, empirical probes precede the design, an approval gate guards implementation, and a conformance audit re-checks the branch after any design change. State persists in .episteme/DESIGN-<slug>.md. Args: <ticket|area> [--design-file <path>] [--lite]"
---

<!-- Template consumed by opencode/dotnet-episteme.js: {{PLUGIN_ROOT}} is
     substituted at load time. Do not copy this file into an OpenCode command
     directory - the placeholder would reach the model raw. -->

# /dotnet-refactor — orchestrated design loop

You are the orchestrator. Do not enumerate or trace code yourself - dispatch session-blind workers (`refactor-cartographer`, `refactor-tracer`, `refactor-surveyor`, `refactor-conformance-auditor` `task` calls), persist their artifacts, and hold the gates. **No code is written or edited until the design is approved at the Phase D gate.**

Arguments given: `$ARGUMENTS`

## Step 0 — Resolve target and state

- Target: a ticket id or an area description. Slug it (e.g. `yb-1234`).
- State file: `--design-file` if given, else `.episteme/DESIGN-<slug>.md` at the repository root (create `.episteme/` if missing).
- **If it exists**, route on its frontmatter - the file is the source of truth, never conversation memory: `status: done` → nothing to resume; `status: blocked` → restate the blocker and ask; `status: awaiting-approval` → re-present the design at the Phase D gate; `status: active` → resume at the phase in `phase:`. OpenCode has no reload hook: re-read the file yourself at the start of every session.
- **If not**, create it with frontmatter (`target`, `phase: R`, `status: active`, `design_revision: 0`) and sections `Map`, `Traces`, `Probes`, `Design (approved)`, `Decision log`, `Deferred`, `Todo`, `Review findings`. Keep the frontmatter current and flush worker artifacts to the file the moment they return. `Decision log` and `Deferred` are append-only.
- Invariant pack: extract the 5-15 lines of project-specific design invariants relevant to the target (CLAUDE.md, architecture docs). Workers receive this pack and artifact data only - never conversation narration.

## Step 1 — Phase R: recall

State to the user, in 3-5 bullets, the standing rules plus anything in the Decision log: fix the **invariant, not the instance**; **dataflow over references** (what value reaches this branch during an outage, not who uses this type); **distrust names after changing behavior**; **empirical before architectural**.

## Step 2 — Phases E+T: enumerate and trace

**Lighter mode** for small targets (one service, a handful of paths - `--lite`, or your own judgment; when unsure run the full pipeline): launch a single `refactor-surveyor` `task` call that maps and traces in one pass. It reports `ESCALATE: <reason>` when the area turns out bigger than one pass should carry - then rerun the full fan-out below.

**Full mode**: launch `refactor-cartographer` `task` calls - one per service, all in one message - with repository root, target, invariant pack, and scope hints. Then `refactor-tracer` per path group the cartographers propose, with the map rows in scope.

Either way: write the map into `Map` and the hop-chain tables into `Traces` verbatim, advance `phase`, and show the user the map and the anomaly list. The map is the scope: a touchpoint discovered later means the map was wrong - update the map first.

## Step 3 — Phase P: probe

Where the design depends on external/vendor behavior, verify it empirically **before** designing - yourself, in the main session (probes may need credentials or VPN the workers must not have). A path that cannot be produced gets a ready-to-run skipped test, never an assumption. Persist results in `Probes`.

## Step 4 — Phase D: design gate (STOP)

Write the design in prose: the invariant being established, type/structure changes, the full touchpoint list with what changes at each, alternatives with a recommendation. Set `status: awaiting-approval`, present it, and stop with exactly: **1. Approve and continue / 2. Approve and stop (a fresh session resumes at Phase I from the file alone) / 3. Revise**. On approval record the design under `Design (approved)` inside a `<frozen-after-approval>` fence, log the decision, derive `Todo` (one item per touchpoint), set `phase: I`, `status: active`. The fence is locked: only an explicit user decision may change it.

## Step 5 — Phase I: implement

Work through `Todo`, checking items off. Apply the design to **every** touchpoint. Out-of-scope findings go to `Deferred`, never fixed inline. **If the design must change mid-flight**: stop, get explicit user approval, update the fenced design, bump `design_revision`, then launch `refactor-conformance-auditor` with the state-file path and how to produce the branch diff - fix every nonconformance before moving forward. After 3 audit rounds in one loop, halt and escalate. Judge the branch by its diff, never by your memory of what you wrote.

## Step 6 — Phase V: verify

Tests assert at the boundary the task is about (serialized payloads, wire status, log text), never only a result type. Prove a regression test fails when the fix is removed. Run non-CI integration suites the change touches. Then run `/dotnet-review` on the working diff; record the outcome in `Review findings`.

## Step 7 — Phase F: final mechanical pass

Re-read the full diff hunk by hunk against the host repo's rules: comment style, stale doc labels / log texts / XML summaries, unused usings, docs updated where behavior changed. Set `status: done`.

Worker restrictions: the `refactor-*` subagents are registered read-only (`{{PLUGIN_ROOT}}/docs/reviewer-restrictions.md`); they search with grep/glob/read tools and stay blind to this session - that blindness is the anti-anchoring mechanism, not an accident.
