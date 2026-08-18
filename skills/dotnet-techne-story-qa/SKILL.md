---
name: dotnet-techne-story-qa
description: Use when verifying a story or ticket implementation against its spec and acceptance criteria - per-AC verdicts with file:line evidence and proving tests, code reuse and design conformance, dead code, stale docs, comment discipline. Finds the spec from an explicit path, the ticket key in the branch name, or plan files - and asks rather than guessing. For defect-hunting code review use dotnet-techne-code-review instead. Keywords: story QA, acceptance criteria, spec conformance, AC coverage, definition of done, dead code check, reuse check, comment check, verify story, QA this branch.
---

# Story QA (single context)

Check an implementation against the story that asked for it. Answer one question: **do the
acceptance criteria hold, and what will hurt later?** Every answer carries a `file:line`. Every
finding survives an attempt to kill it before it reaches the reader. This is the portable
single-context variant; hosts with subagent support run the same dimensions as a parallel
fan-out (Claude Code: `/dotnet-qa`; Codex: the `dotnet-techne-qa-pipeline` skill; OpenCode:
`/dotnet-qa`). A repo-local QA skill, when one exists, is tuned to that repo - prefer it there.

## Boundary

Code review (`dotnet-techne-code-review`) hunts defects across a diff. This checks a diff
against a **contract**: acceptance criteria, reuse, design conformance, dead code. Same branch,
different question - run both when the story is large. **Report findings. Change nothing.**

## Hard gates

Stop and fix these before continuing; they are not style preferences.

1. **Story reference unclear -> ask.** Never guess a ticket (see spec discovery below). A QA
   check against the wrong story is worse than none.
2. **No reference -> no claim.** Cut the sentence instead of softening it.
3. **No break test -> no finding.** An unfalsified finding stays in your notes.
4. **Read before you judge.** Booting, building, and running beats inferring from source.

## Core workflow

1. **Target**: the story diff - current branch vs its merge-base with the default branch, unless
   the user names another target. No diff - halt and say so. List the changed files with line
   counts; read the production code whole for small stories, whole-in-blast-radius for large ones.
2. **Spec**: resolve via [references/spec-discovery.md](references/spec-discovery.md) and write
   the AC list down verbatim before reading code (judging against a remembered AC drifts toward
   the code). No-spec mode only when the user explicitly chooses it; announce the skipped
   acceptance dimension.
3. **Contract source of truth**: if the ticket names an external schema, consumer test suite,
   WSDL, or OpenAPI document, read it - and read the *consumer*, not just the schema. This is
   the step that separates "looks right" from "verified".
4. **Acceptance audit** (spec mode only): for each AC, verdict IMPLEMENTED / PARTIAL / MISSING
   with file:line evidence, plus the proving test - read the test before claiming what it
   covers; a type-only assertion proves nothing behavioral. **Run it**: build the project, run
   the tests, record the counts and warnings - a passing suite you executed outranks a test
   file you read. Sweep exhaustively where the cost is low (all 23 hand-written names, not a
   sample). Check constraints and out-of-scope declarations; cross-check claimed file/task
   lists against `git diff --name-only`. The story narrative is testimony, not evidence.
5. **Reuse and design**: reinvented helpers (search the solution for existing equivalents),
   missing adoption of the change's own new behavior (needs a supersession signal), conformance
   to the project's established patterns and siblings, and the convention this foundation sets
   that later stories will copy.
6. **Dead code**: deletion check (removed code whose contract was neither re-established nor
   retired), newly-dead code nothing reaches (verify with solution-wide searches including
   tests), stale words - docs, log texts, test names describing behavior that no longer exists.
7. **Comment discipline**: check every comment the diff adds or touches against
   [references/comment-rules.md](references/comment-rules.md) (host-repo conventions override
   it - say which you applied). Record the exact replacement line (or "delete") per finding.
8. **Negative space**: the strongest findings often live outside the AC list. What does the
   running system need that no story covers? What does the story defer, and does the deferral
   fail loudly or silently? Tag these `backlog`.
9. **Falsify every finding**: apply [references/falsification.md](references/falsification.md)
   - build the strongest argument each finding is wrong, test it, and record survived / weakened
   / dead. Expect to kill some of your own; disproved suspicions go in Dropped, which is an
   honesty signal, not a footnote.
10. **Report** per [references/qa-output-contract.md](references/qa-output-contract.md) - gate
    and verdict line first, then summary, AC coverage, findings (ranked by consequence),
    Dropped, Checked, Yours to call. Persist `.episteme/QA-<slug>.md` unless the user asks for
    report-only. Afterwards you may offer to apply the comment-line fixes; apply them ONLY after
    the user explicitly confirms.
