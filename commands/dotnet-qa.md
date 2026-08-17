---
description: Story QA - multi-agent verification of a story implementation against its spec: per-AC verdicts, code reuse and design conformance, dead code, then adversarial maintainer verification. Finds the spec by explicit path, ticket key in the branch name, or plan files - and asks rather than guessing. Verdict persists to .episteme/QA-<slug>.md.
argument-hint: "[ticket|branch|--spec <path>] [--base <branch>] [--model sonnet|opus|fable] e.g. YB-1234 or --spec docs/specs/foo.md"
---

# /dotnet-qa — story QA against the spec

You are the orchestrator. Do not audit code yourself - resolve the spec, dispatch, then format
the result. Boundary: `/dotnet-review` hunts defects in a diff; this command verifies a story
against its **spec** (acceptance criteria, reuse, design conformance, dead code). Same branch,
different question.

Arguments given: `$ARGUMENTS`

## Step 1 — Resolve target

- Story diff: the current branch vs its merge-base with the default branch, unless a branch or
  ticket argument or `--base` says otherwise. Slug the story (ticket key if known, else branch
  name) - it names the verdict file.
- If there is nothing to diff, halt and say so.

## Step 2 — Resolve the spec (cascade)

Follow `${CLAUDE_PLUGIN_ROOT}/skills/dotnet-techne-story-qa/references/spec-discovery.md`
exactly: explicit `--spec`/pasted spec → ticket key from argument or branch name (Jira-style
`[A-Z][A-Z0-9]+-\d+`; fetch via an issue-tracker MCP tool when one is available) → repo
artifacts (`.episteme/DESIGN-*.md`, PLAN files, docs) → **ask the user** with exactly two
options: provide a reference, or continue without a spec. Never infer no-spec mode from a
missing path.

Build the spec pack (numbered ACs, constraints, out-of-scope notes, claimed file/task lists,
source, issue links and relevant ticket comments as one-liners, and the **contract source of
truth** if the ticket names one - an external schema, consumer test suite, WSDL, or OpenAPI
document, plus the consumer of the API, not just its schema; fetch its content yourself and
put the relevant excerpts in the pack - the workers have no web access and cannot dereference
a URL) and a practices pack: 5-15 lines of the project's established conventions relevant to
this change (CLAUDE.md, architecture docs, `.claude/docs/*`). When the host repo ships its own
QA skill or QA instructions (e.g. a `.claude/skills/*qa*` check), fold its rules into the
practices pack and follow its report conventions where they conflict - repo-local wins. Workers
receive these packs and the diff - never conversation narration; they stay blind to the session.

## Step 3 — Preferred: run the bundled workflow

If the Workflow tool is available, invoke the plugin's script (this command is your opt-in):

```
Workflow({
  scriptPath: "${CLAUDE_PLUGIN_ROOT}/workflows/dotnet-qa.js",
  args: {
    target: "<branch or ticket>",
    base: "<base branch or omit>",
    specPack: "<the spec pack, or omit in no-spec mode>",
    practicesPack: "<the practices pack or omit>",
    model: "<only if --model was passed>",
    pluginRoot: "${CLAUDE_PLUGIN_ROOT}"
  }
})
```

The script does everything: a scout captures the story diff, the three QA lanes run in parallel
(acceptance is skipped in no-spec mode - the script logs it; degradation is announced, never
silent), findings are deduped in code, and the review maintainer falsifies them against the
maintainer playbook plus the QA falsification counter-arguments - it also challenges AC
verdicts whose evidence does not hold (advisory `acDisputes`; the table is never rewritten) and
may upgrade a finding that got worse under its check. A failed maintainer gates CONCERNS, never
a silent PASS. Intermediate findings never enter this conversation - you receive
`{gate, verdictLine, acSummary, acCoverage, acDisputes, findings, refuted, lanesFailed, maintainerFailed, noSpec}`.

When it returns, run the suite (Step 4b) and go to Step 5.

## Step 4 — Fallback: Task-call orchestration

Only when the Workflow tool is unavailable. Capture the diff once (read-only git), then:

1. **Launch the lanes in ONE message** (parallel Task calls): `dotnet-episteme-skills:qa:acceptance`
   (only in spec mode) with the spec pack, `qa:reuse-design` with the practices pack,
   `qa:dead-code` with the comment-rules path
   (`${CLAUDE_PLUGIN_ROOT}/skills/dotnet-techne-story-qa/references/comment-rules.md`).
   Each prompt carries the repo root, the diff, and the finding block format
   (Severity / Area / Owner / Location / Evidence / Impact / Fix / Confidence). In no-spec mode
   state in your output that the acceptance lane was skipped and why.
2. **Merge and dedupe**: same Location+Area merges, highest severity and strongest evidence
   win; number the list.
3. **Maintainer falsification**: launch `dotnet-episteme-skills:review:maintainer` with the
   numbered findings, the AC coverage table, the spec pack (its issue links back the "another
   ticket covers it" counter-argument; its full criterion texts back any AC dispute), the diff
   scope, and both
   `${CLAUDE_PLUGIN_ROOT}/skills/dotnet-techne-code-review/references/maintainer-playbook.md`
   and `${CLAUDE_PLUGIN_ROOT}/skills/dotnet-techne-story-qa/references/falsification.md`.
   Launch it even when the lanes returned zero findings in spec mode - an all-IMPLEMENTED table
   is exactly the result that needs an adversary. Apply verdicts: drop REFUTED (keep the
   one-line list), adjust DOWNGRADED and UPGRADED. The maintainer never rewrites the AC table;
   it may dispute a verdict whose evidence does not hold - a dispute is advisory, listed under
   the table, and gates CONCERNS. If the maintainer fails to launch, say so and gate CONCERNS -
   unfalsified findings never read as a clean result.

## Step 4b — Run the suite (the workers cannot)

The QA lanes are read-only sandboxed - they judge tests statically but cannot build or run
them. You can. Build the touched projects and run the relevant tests yourself, record the
counts and warnings, and feed the result into the report's Summary and Checked table. A passing
suite you executed outranks a test file the acceptance lane only read; a failing build turns a
PASS into a FAIL regardless of the AC table. Skip only when there is no runnable project or the
user says so.

## Step 5 — Gate, report, persist

Format the output exactly per
`${CLAUDE_PLUGIN_ROOT}/skills/dotnet-techne-story-qa/references/qa-output-contract.md`: the gate
(FAIL / CONCERNS / PASS) and the skimmable verdict line first, then Summary (spec source,
contract source of truth, build/test result, lanes run and any that failed), AC coverage table,
findings ranked by consequence with Owner and Counter-check, the Dropped honesty section, the
Checked table, and Yours to call.

Persist the verdict to `.episteme/QA-<slug>.md` (create `.episteme/` if missing) with
frontmatter `target`, `spec_source`, `gate`, `date` - skip only if the user asked for
report-only. The QA itself changes no code: apply fixes only if the user asks afterwards
(offering to is fine).

One narrow exception - comment findings (area: comments, checked against
`${CLAUDE_PLUGIN_ROOT}/skills/dotnet-techne-story-qa/references/comment-rules.md`, host-repo
conventions winning): after the report, offer to apply the comment-line fixes (each finding
carries the exact replacement line or "delete"). Apply them yourself ONLY after the user
explicitly confirms - the workers never edit, and a decline ends it there.
