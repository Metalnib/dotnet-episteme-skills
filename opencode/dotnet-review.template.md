---
description: "Multi-agent .NET code review - five reviewers in parallel (correctness, performance, security/observability, data/messaging/integration, generalist), then an adversarial maintainer pass that refutes weak findings. Args: [branch|commit-range|--staged|(empty = uncommitted changes)] [--cynical]"
---

<!-- Template consumed by opencode/dotnet-episteme.js: {{PLUGIN_ROOT}} and
     {{TIER_GUIDANCE}} are substituted at load time. Do not copy this file into
     an OpenCode command directory - the placeholders would reach the model raw. -->

# /dotnet-review — orchestrated multi-agent .NET review

You are the orchestrator. Do not review code yourself - dispatch, then format the result.

Arguments given: `$ARGUMENTS`

## Step 1 — Resolve target and mode

- Target: a branch name, commit range, `--staged`, or (if empty) the current uncommitted changes. For a document/spec review target, use the `dotnet-techne-code-review` skill instead of this command.
- Mode: **Cynical** if `--cynical` was passed or the user's request language is explicitly skeptical ("tear this apart", "assume this is broken", "devil's advocate"); otherwise **Standard**.
- Intent pack (maintainer only, never shown to reviewers): 2-5 lines summarizing what was built and any deliberate trade-offs decided in this session, if you have that context.

## Step 2 — Dispatch

Gather context once with the skill scripts (`{{PLUGIN_ROOT}}/skills/dotnet-techne-code-review/scripts/`: `list-changes.sh`, `branch-diff.sh`, `review-context.sh`), build a compact context pack (changed files, diff or per-file summaries above ~2000 lines, component types, repo root), then:

1. **Size the change** and state the tier it deserves: a security, public-API, data or messaging surface → at least opus-class; ≤5 files and ≤200 LOC with no such surface → sonnet-class; >50 files or cross-repo blast radius → frontier-class. {{TIER_GUIDANCE}}
2. **Launch the five reviewers in ONE message** (parallel `task` calls) with `subagent_type` `review-correctness` / `review-performance` / `review-security-observability` / `review-data-messaging` (covers DB, RabbitMQ, and HTTP integration: endpoints, adapters, consumers) / `review-generalist` (no lane - catches what falls between the specialists). Each delegation prompt carries: mode (Cynical demands ≥5 falsified hypotheses within their scope), the context pack including the diff (still include it - reviewer bash is permission-restricted to read-only git, and the inline diff saves redundant calls), the absolute path to `{{PLUGIN_ROOT}}/skills/dotnet-techne-code-review/references/domain-checklists.md` plus that agent's assigned sections (generalist gets none), and the finding block format (Severity / Area / Location / Evidence / Impact / Fix / Confidence). Reviewers stay blind to the session - no conversation history or design rationale in their prompts. Skip data-messaging only when DB, messaging, AND HTTP-integration surfaces are all provably absent; never skip the generalist; when in doubt, launch everything.
3. **Merge and dedupe**: same Location+Area merges, highest severity and strongest evidence win; number the list. Zero findings in Cynical mode → re-launch the most relevant reviewer once with a different lens before concluding; never invent findings.
4. **Maintainer pushback**: launch `review-maintainer` with the numbered findings, the context pack, the intent pack, and `{{PLUGIN_ROOT}}/skills/dotnet-techne-code-review/references/maintainer-playbook.md`. Apply verdicts: drop REFUTED (keep a one-line "Refuted by maintainer" list), adjust DOWNGRADED, record rationales in Counter-check.

There is no workflow runtime here: the fan-out, the dedupe, and the maintainer pass are yours to drive with parallel `task` calls. Findings from the reviewers do enter this conversation, so quote them only through the report below - never paste raw reviewer output.

## Step 3 — Report

Format the final output exactly per `{{PLUGIN_ROOT}}/skills/dotnet-techne-code-review/references/output-contract.md` (Summary with mode and overall risk, Findings, Quick wins, Follow-ups, halt conditions). State in the Summary: 5 parallel reviewers + maintainer verification, and which model they ran on. List refuted findings at the end, one line each with the refutation evidence.

The review itself changes nothing: apply fixes only if the user asks afterwards (offering to is fine).
