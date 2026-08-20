---
description: Multi-agent .NET code review - up to five reviewers in parallel (correctness, performance, security/observability, data/messaging/integration, generalist), then an adversarial maintainer pass that refutes weak findings. Runs the bundled dotnet-review workflow; reviewer model scales with change size.
argument-hint: "[branch|commit-range|--staged|(empty = uncommitted changes)] [--cynical] [--model sonnet|opus|fable] [--effort low|medium|high|xhigh|max]"
---

# /dotnet-review — orchestrated multi-agent .NET review

You are the orchestrator. Do not review code yourself - dispatch, then format the result.

Arguments given: `$ARGUMENTS`

## Step 1 — Resolve target and mode

- Target: a branch name, commit range, `--staged`, or (if empty) the current uncommitted changes. For a document/spec review target, use the `dotnet-techne-code-review` skill instead of this command. For verifying a story implementation against its spec/acceptance criteria, use `/dotnet-qa` - this command hunts defects, not spec conformance.
- Effort: pass `--effort <level>` straight through if given. Without it the script scales reasoning effort with the change size, so only override when the user asks.
- Mode: **Cynical** if `--cynical` was passed or the user's request language is explicitly skeptical ("tear this apart", "assume this is broken", "devil's advocate"); otherwise **Standard**.
- Intent pack (maintainer only, never shown to reviewers): 2-5 lines summarizing what was built and any deliberate trade-offs decided in this session, if you have that context.

## Step 2 — Preferred: run the bundled workflow

If the Workflow tool is available, invoke the plugin's script (this command is your opt-in):

```
Workflow({
  scriptPath: "${CLAUDE_PLUGIN_ROOT}/workflows/dotnet-review.js",
  args: {
    target: "<resolved target>",
    cynical: <bool>,
    model: "<only if --model was passed>",
    effort: "<only if --effort was passed>",
    pluginRoot: "${CLAUDE_PLUGIN_ROOT}",
    intentPack: "<intent pack or omit>"
  }
})
```

The script does everything: a scout agent sizes the change and resolves it to two pinned commit SHAs plus classified file lists (it emits no diff text - each reviewer produces the diff itself from a read-only `git diff` command the script assembles, so the diff never passes through a model's output), the script picks a model tier per reviewer in code (base from size: small → sonnet, medium → session model at medium effort, large or cross-repo → opus; a security/public-API/data surface boosts only the reviewer that owns it to at least opus; the maintainer matches the strongest reviewer). Opus is the ceiling the script picks on its own - `fable` runs only when the user passes `--model fable`. Reasoning effort scales on the same breakpoints (medium, then high above 20 files or 1000 LOC, then xhigh above 50 files or 3000 LOC; Cynical adds one step; the maintainer matches the reviewers), and `--effort` overrides it, fans out up to five reviewers in parallel (four specialists + a generalist with no lane; the four specialists get tests and generated files filtered out of their diff, and are skipped entirely when the change has no source files), dedupes in code, and runs the maintainer verification (per-finding parallel above 10 blocking findings). Intermediate findings never enter this conversation - you receive `{tiers, maintainerTier, sizing, mode, scope, findings, refuted, reviewersFailed, reviewersSkipped}`.

When it returns, go to Step 4.

## Step 3 — Fallback: Task-call orchestration

Only when the Workflow tool is unavailable or disabled. Gather context once with the skill scripts (`${CLAUDE_PLUGIN_ROOT}/skills/dotnet-techne-code-review/scripts/`: `list-changes`, `branch-diff`, `review-context`; `.ps1` variants on Windows), build a compact context pack (changed files, diff or per-file summaries above ~2000 lines, component types, repo root), then:

1. **Size the change** and pick the model tier with the same table the script uses (security/public-API/data/messaging surface → at least opus; ≤5 files and ≤200 LOC with no such surface → sonnet; >20 files, >1000 LOC or cross-repo blast radius → opus; otherwise inherit). Never pick `fable` yourself - it runs only when the user passes `--model fable`. If unsure, ask the user; if you cannot ask, err one tier up. `--model` skips this. The maintainer is never on a weaker model than the reviewers. Pass the tier as the per-invocation `model` parameter on each Task call - agent files deliberately omit `model:`.
2. **Launch the five reviewers in ONE message** (parallel Task calls) with `subagent_type` `dotnet-episteme-skills:review:correctness` / `review:performance` / `review:security-observability` / `review:data-messaging` (covers DB, RabbitMQ, and HTTP integration: endpoints, adapters, consumers) / `review:generalist` (no lane - catches what falls between the specialists). Each delegation prompt carries: mode (Cynical demands ≥5 falsified hypotheses within their scope), the context pack including the diff (still include it - reviewers' Bash is hook-restricted to read-only git, and the inline diff saves redundant calls), the absolute path to `${CLAUDE_PLUGIN_ROOT}/skills/dotnet-techne-code-review/references/domain-checklists.md` plus that agent's assigned sections (generalist gets none), and the finding block format (Severity / Area / Location / Evidence / Impact / Fix / Confidence). Reviewers stay blind to the session - no conversation history or design rationale in their prompts. Skip data-messaging only when DB, messaging, AND HTTP-integration surfaces are all provably absent; never skip the generalist; when in doubt, launch everything.
3. **Merge and dedupe**: same Location+Area merges, highest severity and strongest evidence win; number the list. Zero findings in Cynical mode → re-launch the most relevant reviewer once with a different lens before concluding; never invent findings.
4. **Maintainer pushback**: launch `dotnet-episteme-skills:review:maintainer` with the numbered findings, the context pack, the intent pack, and `${CLAUDE_PLUGIN_ROOT}/skills/dotnet-techne-code-review/references/maintainer-playbook.md`. Apply verdicts: drop REFUTED (keep a one-line "Refuted by maintainer" list), adjust DOWNGRADED, record rationales in Counter-check.

## Step 4 — Report

Format the final output exactly per `${CLAUDE_PLUGIN_ROOT}/skills/dotnet-techne-code-review/references/output-contract.md` (Summary with mode and overall risk, Findings, Quick wins, Follow-ups, halt conditions). State in the Summary: how many reviewers ran in parallel + maintainer verification, and the model tiers plus the reasoning effort used, with a one-line reason. When `reviewersSkipped` is non-empty, name each skipped reviewer and its reason there - a skip that only reaches the log reads as "reviewed and clean". Same for `reviewersFailed`. List refuted findings at the end, one line each with the refutation evidence.

The review itself changes nothing: apply fixes only if the user asks afterwards (offering to is fine).
