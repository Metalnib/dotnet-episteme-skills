---
name: dotnet-techne-review-pipeline
description: "Use when the user asks for a multi-agent .NET code review - five parallel reviewers (correctness, performance, security/observability, data/messaging/integration, generalist) plus an adversarial maintainer that refutes weak findings. Requires the review agent roles registered by scripts/install-codex.sh. Keywords: dotnet review pipeline, multi-agent review, parallel reviewers, maintainer pushback, review this branch, cynical review."
license: MIT
compatibility: Requires a Codex CLI with multi-agent tools enabled (agents.enabled, default true) and the review roles registered in config.toml. Without the roles, fall back to the single-context dotnet-techne-code-review skill.
---

# .NET review pipeline (multi-agent)

You are the orchestrator. Do not review the code yourself - dispatch, then format the result.

Paths below are relative to this skill's base directory. The checklists, scripts, and contracts live in the sibling review skill: `../../../skills/dotnet-techne-code-review/`.

## Step 0 - Check the roles exist

The pipeline needs six roles: `review-correctness`, `review-performance`, `review-security-observability`, `review-data-messaging`, `review-generalist`, `review-maintainer`. List your available agent roles first. If they are missing, stop and tell the user to run `scripts/install-codex.sh` from the plugin, and use the `dotnet-techne-code-review` skill for a single-context review in the meantime.

## Step 1 - Resolve target and mode

- Target: a branch name, commit range, staged changes, or (default) the current uncommitted changes. For a document or spec review, use `dotnet-techne-code-review` instead.
- Mode: **Cynical** if the user's language is explicitly skeptical ("tear this apart", "assume this is broken", "devil's advocate"); otherwise **Standard**.
- Intent pack (maintainer only, never shown to reviewers): 2-5 lines on what was built and any deliberate trade-offs decided in this session, if you have that context.

## Step 2 - Gather context once

Run the helper scripts in `../../../skills/dotnet-techne-code-review/scripts/` (`list-changes.sh`, `branch-diff.sh`, `review-context.sh`; `.ps1` variants on Windows) and build one compact context pack: changed files, the diff (per-file summaries above ~2000 lines), component types, repo root.

## Step 3 - Fan out

Spawn the five reviewers **in parallel**, one per role, each with:

- the mode (Cynical demands at least 5 falsified hypotheses within their scope),
- the context pack including the diff - the roles run read-only, so the inline diff saves them redundant work,
- the absolute path to `../../../skills/dotnet-techne-code-review/references/domain-checklists.md` plus that role's assigned sections (the generalist gets none - it hunts what falls between the lanes),
- the finding block format: Severity / Area / Location / Evidence / Impact / Fix / Confidence.

Reviewers stay blind to the session: no conversation history, no design rationale. Skip `review-data-messaging` only when database, messaging, AND HTTP-integration surfaces are all provably absent. Never skip the generalist. When in doubt, launch everything.

Model tier: a security, public-API, data or messaging surface deserves a strong model; a small change with no such surface (<=5 files, <=200 LOC) can run cheaper. Set it per spawn, or leave `agents.default_subagent_model` to decide. The maintainer is never weaker than the reviewers.

## Step 4 - Merge, then let the maintainer attack

Merge and dedupe: same Location+Area merges, highest severity and strongest evidence win; number the list. Zero findings in Cynical mode - relaunch the most relevant reviewer once with a different lens before concluding. Never invent findings.

Then spawn `review-maintainer` with the numbered findings, the context pack, the intent pack, and `../../../skills/dotnet-techne-code-review/references/maintainer-playbook.md`. Apply its verdicts: drop REFUTED (keep a one-line "Refuted by maintainer" list), adjust DOWNGRADED, record rationales under Counter-check.

## Step 5 - Report

Format exactly per `../../../skills/dotnet-techne-code-review/references/output-contract.md`: Summary (mode, overall risk), Findings, Quick wins, Follow-ups, halt conditions. State in the Summary that five parallel reviewers plus maintainer verification ran, and on which models. List refuted findings at the end, one line each with the refutation evidence.

The review changes nothing: apply fixes only if the user asks afterwards (offering is fine).
