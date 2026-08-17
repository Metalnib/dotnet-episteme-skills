---
description: "Story QA - verify a story implementation against its spec: per-AC verdicts, code reuse and design conformance, dead code, then adversarial maintainer verification. Finds the spec by explicit path, ticket key in the branch name, or plan files - and asks rather than guessing. Args: [ticket|branch|--spec <path>] [--base <branch>]"
---

<!-- Template consumed by opencode/dotnet-episteme.js: {{PLUGIN_ROOT}} is
     substituted at load time. Do not copy this file into an OpenCode command
     directory - the placeholder would reach the model raw. -->

# /dotnet-qa — story QA against the spec

You are the orchestrator. Do not audit code yourself - resolve the spec, dispatch, then format the result. Boundary: `/dotnet-review` hunts defects in a diff; this command verifies a story against its **spec**. Same branch, different question.

Arguments given: `$ARGUMENTS`

## Step 1 — Resolve target

The story diff: the current branch vs its merge-base with the default branch, unless a branch or ticket argument or `--base` says otherwise. Slug the story (ticket key if known, else branch name). Nothing to diff - halt and say so.

## Step 2 — Resolve the spec (cascade)

Follow `{{PLUGIN_ROOT}}/skills/dotnet-techne-story-qa/references/spec-discovery.md` exactly: explicit `--spec`/pasted spec → ticket key from the argument or branch name (Jira-style `[A-Z][A-Z0-9]+-\d+`; fetch via an issue-tracker MCP tool when one is available) → repo artifacts (`.episteme/DESIGN-*.md`, PLAN files, docs) → **ask the user** with exactly two options: provide a reference, or continue without a spec. Never infer no-spec mode from a missing path.

Build the spec pack (numbered ACs, constraints, out-of-scope notes, claimed file/task lists, source, issue links and relevant ticket comments as one-liners, and the **contract source of truth** if the ticket names one - an external schema, consumer test suite, WSDL, or OpenAPI document, plus the consumer of the API; fetch its content yourself and put the relevant excerpts in the pack - the lanes have no web access and read outside the project only for this plugin's files, so a pointer they cannot dereference silently skips the check) and a practices pack (5-15 lines of the project's established conventions; when the host repo ships its own QA skill or QA instructions, fold its rules in - repo-local wins). Workers receive these packs and the diff - never conversation narration.

## Step 3 — Dispatch

Capture the story diff once (read-only git). Every lane runs on the current session model - the QA does not size a tier; the session model is enough for these validations.

1. **Launch the lanes in ONE message** (parallel `task` calls): `qa-acceptance` (only in spec mode) with the spec pack, `qa-reuse-design` with the practices pack, `qa-dead-code` with the comment-rules path (`{{PLUGIN_ROOT}}/skills/dotnet-techne-story-qa/references/comment-rules.md`; host-repo conventions override it). Each delegation prompt carries: the repo root, the diff (reviewer bash is permission-restricted to read-only git, so the inline diff saves redundant calls), and the finding block format (Severity / Area / Owner / Location / Evidence / Impact / Fix / Confidence). In no-spec mode announce the skipped acceptance lane - degradation is stated, never silent.
2. **Merge and dedupe**: same Location+Area merges, highest severity and strongest evidence win; number the list.
3. **Maintainer falsification**: launch `review-maintainer` with the numbered findings, the AC coverage table, the spec pack (its issue links back the "another ticket covers it" counter-argument; its full criterion texts back any AC dispute), the diff scope, and both `{{PLUGIN_ROOT}}/skills/dotnet-techne-code-review/references/maintainer-playbook.md` and `{{PLUGIN_ROOT}}/skills/dotnet-techne-story-qa/references/falsification.md`. Launch it even when the lanes returned zero findings in spec mode - an all-IMPLEMENTED table is exactly the result that needs an adversary. Apply verdicts: drop REFUTED (keep a one-line Dropped list with the refutation), adjust DOWNGRADED and UPGRADED. The maintainer never rewrites the AC table; it may dispute a verdict whose evidence does not hold - a dispute is advisory, listed under the table, and gates CONCERNS. If the maintainer fails to launch, say so and gate CONCERNS - unfalsified findings never read as a clean result.

Findings from the lanes do enter this conversation, so quote them only through the report below - never paste raw lane output.

## Step 3b — Run the suite (the lanes cannot)

The QA lanes are read-only - they judge tests statically but cannot build or run them. You can. Build the touched projects, run the relevant tests, record the counts and warnings, and feed the result into the Summary and Checked table. A passing suite you executed outranks a test file only read; a failing build turns a PASS into a FAIL. Skip only when there is no runnable project or the user says so.

## Step 4 — Gate, report, persist

Format exactly per `{{PLUGIN_ROOT}}/skills/dotnet-techne-story-qa/references/qa-output-contract.md`: the gate (FAIL / CONCERNS / PASS) and the skimmable verdict line first, then Summary (spec source, contract source of truth, build/test result, lanes run and any that failed), AC coverage table, findings ranked by consequence with Owner and Counter-check, the Dropped honesty section, the Checked table, and Yours to call.

Persist the verdict to `.episteme/QA-<slug>.md` (create `.episteme/` if missing) with frontmatter `target`, `spec_source`, `gate`, `date` - skip only if the user asked for report-only. The QA changes no code: apply fixes only if the user asks afterwards (offering to is fine). One narrow exception - comment findings (area: comments): offer to apply the comment-line fixes (each finding carries the exact replacement line or "delete"), and apply them yourself ONLY after the user explicitly confirms. The workers never edit.
