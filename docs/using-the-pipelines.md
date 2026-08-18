# Using the pipelines

The plugin ships three multi-agent pipelines. This page walks through what you type, what
happens, what you will be asked, and what ends up on disk. Command names below are the Claude
Code / OpenCode ones; on Codex you ask for the same thing in your own words and the matching
pipeline skill picks it up.

| You want to know | Run |
|---|---|
| "Is this code broken?" | `/dotnet-review` |
| "Did we build what the ticket asked for?" | `/dotnet-qa` |
| "How do I change this safely?" (design first) | `/dotnet-refactor` |

They are complementary - a story branch can go through all three.

## /dotnet-review — find defects in a change

Point it at a branch, a commit range, `--staged`, or nothing for your current uncommitted
changes:

```
/dotnet-review feature/YB-1234-payment-retry
/dotnet-review --staged --cynical
```

A scout sizes the change and captures the diff. Five reviewers then read it in parallel, each
in a fresh context with its own specialty: correctness and API design, performance and AOT,
security and observability, data access and messaging, plus a generalist hunting what falls
between those lanes. Their findings are merged, and an adversarial "maintainer" agent tries to
refute each one - anything it can kill with file:line evidence never reaches you. You get a
single report: findings with evidence, impact, a concrete fix, and the maintainer's
counter-check on each.

Add `--cynical` when you want the harshest pass (every reviewer must form and falsify defect
hypotheses), `--model opus|fable` to force a tier. The review changes nothing - it reports, you
decide.

## /dotnet-qa — verify a story against its spec

Run it on a story branch, ideally named after the ticket:

```
/dotnet-qa                       # current branch, spec found automatically
/dotnet-qa YB-1234               # explicit ticket
/dotnet-qa --spec docs/specs/payment-retry.md
```

First it finds the spec. It tries, in order: a spec you named, the ticket key in your branch
name (fetched from your issue tracker when an MCP connection exists), and plan/design documents
in the repo. If nothing turns up it asks you - provide a reference, or explicitly continue
without a spec. It never quietly assumes there is no spec.

Then three auditors run in parallel:

- **Acceptance**: every acceptance criterion gets a verdict - IMPLEMENTED, PARTIAL, or MISSING -
  with file:line evidence and the test that proves it (the test is read, not trusted by name).
- **Reuse and design**: did the change reinvent a helper the codebase already has, skip adopting
  its own new behavior somewhere it should, or diverge from how sibling code solves the same
  problem?
- **Dead code and words**: deletions that dropped a contract, additions nothing reaches, stale
  docs and log texts, and comment discipline - comments must say *why*, one line, no ticket
  numbers outside tests, bug-fix tests carry their ticket reference.

The maintainer agent falsifies the findings and challenges any acceptance verdict whose
evidence does not hold when it reads the code (it never rewrites the table - a dispute is
advisory). You get a report that opens with a deterministic gate: **FAIL** (a criterion is
missing or something blocking survived), **CONCERNS** (partial criteria, missing proving tests,
important findings, a disputed verdict, a lane or the maintainer failing to run, or no spec),
or **PASS**. The verdict also lands in `.episteme/QA-<slug>.md` in your repo, so "was this story
QA'd and what did it say" survives the session.

QA changes no code, with one opt-in exception: comment findings come with their exact
replacement line, and after the report you can tell it to apply those - nothing is touched
without your explicit yes.

## /dotnet-refactor — design loop with gates

For refactoring or redesign work that is too big to just start typing:

```
/dotnet-refactor YB-1234
/dotnet-refactor "error handling in OrderService" --lite
```

The loop runs in phases, and the important property is what it will NOT do: **no code is
written until you approve a design.**

1. **Recall** - it states the standing design rules that apply (fix the invariant, not the
   instance; trace dataflow, not references; distrust names after behavior changes; verify
   external behavior empirically).
2. **Enumerate and trace** - session-blind workers build the complete map of the area (every
   outcome-producing branch, every consumer, sibling implementations, the docs and log texts
   attached to the code) and walk each dataflow path end to end, asking at every hop: what
   value arrives here during an outage? On an empty result? On duplicates? This is where dead
   guards and "outage looks like not-found" bugs surface - before any design exists. Small
   target? `--lite` runs one combined worker instead of the full fan-out, and it escalates
   honestly if the area turns out bigger than expected.
3. **Probe** - anything the design would assume about an external system gets verified with a
   real probe first. A probe result is allowed to change the design; that is its purpose.
4. **Design gate** - you get the design in prose: the invariant it establishes, every touchpoint
   from the map, alternatives with a recommendation. Then it stops and asks: approve and
   continue, approve and stop (a fresh session picks up exactly here), or revise.
5. **Implement, verify, final pass** - the approved design is applied to every touchpoint from
   the map, out-of-scope discoveries go to a Deferred list instead of widening the branch, and
   the loop finishes with `/dotnet-review` on the diff plus a mechanical stale-words sweep. If
   the design has to change mid-flight, a conformance auditor re-checks the whole branch
   against the new design before work continues.

Everything lives in `.episteme/DESIGN-<slug>.md`: the map, the traces, the frozen approved
design, the decision log, the todo list. On Claude Code a hook re-injects that file after
`/clear`, compaction, or a restart - the loop never depends on conversation memory. On OpenCode
and Codex the command re-reads the file at session start instead.

## The .episteme folder

Both stateful pipelines write their artifacts to `.episteme/` at the repo root:
`DESIGN-<slug>.md` (refactor loop state) and `QA-<slug>.md` (QA verdicts). Whether to commit or
gitignore the folder is your team's call - the files are useful review artifacts, but they are
working state, not source.

## Worker permissions

Every pipeline worker is read-only and session-blind: no file writes, no web access, no nested
agents, and no conversation history - blind workers cannot inherit the author's assumptions.
Review and QA lanes get read-only git; refactor lanes, which sweep whole solutions, also get
the read-only search tools (rg/fd when installed, grep/find as fallbacks, ls/cat/head/tail/
wc/tree). The full contract and how each tool enforces it:
[reviewer-restrictions.md](reviewer-restrictions.md).
