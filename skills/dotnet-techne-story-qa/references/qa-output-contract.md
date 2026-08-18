# QA Output Contract

The report is short by design. The reader skims the verdict, reads finding 1, and skips the
rest unless it earns attention. Write for that reader. Use this structure exactly.

## Gate and verdict

State both, gate first:

- **Gate** (deterministic, machine-comparable, computed after maintainer verification):
  - **FAIL** - any MISSING acceptance criterion, violated spec constraint, or blocking finding survives.
  - **CONCERNS** - any PARTIAL criterion, criterion without a proving test, or important finding survives; or the QA ran in no-spec mode; or a lane failed; or the maintainer falsification failed to run (unfalsified findings never gate PASS); or the maintainer disputed an AC verdict.
  - **PASS** - everything else.
- **Verdict line** (human, skimmable, fixed vocabulary): `All N ACs met.` / `N of M ACs met.` /
  `AC <n> missed.`, then the finding counts (`1 blocker in the backlog, 3 risks, 1 nit`), then
  optionally one clause of colour (`Code is sound.`). The verdict informs a decision; it does
  not make one.

## Summary

- Story/target reviewed and the diff scope (branch, base).
- Spec source (ticket id, plan file, doc) and the contract source of truth if one was read - or,
  in no-spec mode: "no spec provided (explicitly chosen); acceptance dimension skipped".
- Whether the build and tests were run, with counts - or why not (workers cannot run them; the
  orchestrator/single-context runs the suite). A passing suite you executed outranks a test file read.
- Lanes run, and any lane that failed to launch - the maintainer included. A failed lane means
  the QA may be incomplete; a failed maintainer means the findings are unfalsified. Never
  announce a clean result over either.

## AC coverage (omit in no-spec mode)

| AC | Criterion (short) | Verdict | Evidence | Proving test |

One row per criterion: IMPLEMENTED / PARTIAL / MISSING, file:line evidence, test name or NONE.
The table is the acceptance lane's; the maintainer never rewrites it. When the maintainer
disputed a verdict, keep the row and list the dispute (AC id + rationale) directly beneath the
table - a disputed verdict gates CONCERNS.

## Findings (ranked by consequence, worst first)

Rank by consequence, not by how interesting the finding is - a backlog gap that stops the phase
outranks a clever code smell. Each finding:

- **Severity:** blocking / important / suggestion
- **Area:** acceptance | reuse | design | dead-code | stale-words | comments
- **Owner:** code | backlog | test-suite | docs (routes the finding to who fixes it, without a follow-up conversation)
- **Location:** file + line + type/method
- **Evidence:** short code snippet, existing-member citation, or the searches that found nothing
- **Impact:** what the story fails to deliver, and who gets hurt when
- **Fix:** concrete recommendation
- **Counter-check:** the maintainer's verdict rationale (survived / weakened / got worse, and why)
- **Confidence:** high / medium / low

## Dropped

The disproved suspicions, worst-first: the claim, the refutation, the `file:line` that killed
it. Then the total count. This section is an honesty signal, not a footnote - an empty Dropped
section usually means the falsification pass was not real. When a stronger version of a finding
died and a weaker one survived, record the death here; that correction is worth more to the
reader than the finding.

## Checked

A table, one row per area, with the method and the result - built from the lanes' own Checked
lists plus the build/test run. This is what lets the reader trust the silence: it shows the
areas a reader might otherwise assume were skipped. Include build and test counts.

| Area | How | Result |

## Yours to call

Open points as options and questions, never a task list, never "you must", never a deadline.
Hand over decisions, not orders. Close with a single line stating what you changed. Normally:
`I changed nothing.`

## Writing rules

- Short sentences, simple English, verbs and nouns. One finding fits on one screen; if it needs
  more, it is two findings.
- `Impact:` names who gets hurt and when. Labels take a colon, not a full stop.
- No em dashes; use commas, brackets, or a full stop.
- Never pad. The reader stops at the first paragraph that does not earn its place.

## Halt conditions

- QA changes nothing: never apply fixes as part of the run (offering afterwards is fine).
  One narrow exception - comment-line findings (area: comments): after the report, the
  orchestrator may offer to apply them (each finding carries the exact replacement line or
  "delete") and applies them ONLY after the user explicitly confirms. Workers never edit.
- No diff to review - halt and say so.
- No-spec mode must be explicitly chosen by the user, never inferred from a missing path.
- Never invent findings: an AC table with every criterion IMPLEMENTED and zero findings is a
  valid result.

## Persisted verdict

Write the gate, verdict line, AC table (with any maintainer disputes beneath it), and surviving
findings to `.episteme/QA-<slug>.md` (slug from the ticket key or branch; create `.episteme/`
if missing) with frontmatter `target`, `spec_source`, `gate`, `date`. When a lane or the
maintainer failed to run, or no-spec mode was chosen, say so in the file - the persisted gate
must explain itself without the session. Skip persisting only when the user asks for
report-only.
