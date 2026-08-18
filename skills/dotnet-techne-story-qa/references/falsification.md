# Falsification pass

Run this against every candidate finding before it reaches the report. The goal is not to
soften findings - it is to make the surviving ones load-bearing. In the pipeline the
`review:maintainer` agent applies it; in single-context mode you apply it yourself.

## The move

For each candidate, write the strongest single argument that the finding is wrong. Then test
that argument against the code, the docs, or the running system. Do not settle it by reasoning
alone when a command can settle it.

State the outcome in one word:

| Outcome | Means | Where it goes |
|---|---|---|
| **survived** | the counter-argument failed | Findings, at the severity you assigned |
| **weakened** | the counter-argument landed partly | Findings, one severity lower, with the limit stated |
| **dead** | the counter-argument holds | Dropped |

A finding can also get **worse** under the test. Say so - those are the most valuable. In the
pipeline that is the maintainer's UPGRADED verdict (with the new severity).

## Counter-arguments that work

Reach for these first; they kill real findings often enough to be worth the minute.

- **"The consumer already handles it."** Read the consumer. An assertion may be commented out,
  a caller may tolerate a null, a schema may be unused.
- **"Another ticket covers it."** Read the issue links, the epic, and the sibling stories
  before claiming a backlog gap. In pipeline mode this needs the issue links from the spec
  pack; if they are missing, say the check could not run and leave it to the orchestrator -
  never settle it by assumption.
- **"The convention makes it moot."** If every downstream type follows one style, a missing
  global setting costs nothing. Usually turns a Blocker into a Risk.
- **"The framework does that for you."** Check the default before claiming something is missing
  - options binding, model validation, status-code pages, content negotiation, EF change
  tracking all have surprising defaults.
- **"It cannot reach production."** Read the deploy pipeline before calling it a production risk.
- **"It is unreachable in practice."** Count the operators, environments, and frequency. A real
  hazard with one careful operator is a low Risk, not a Blocker.
- **"The test would have caught it."** Then run the test and watch it pass. Pipeline workers
  cannot run tests (read-only shell) - read the test, state what an execution would settle, and
  leave the run to the orchestrator's suite step instead of counting a read as a run.

## Where the strong version usually dies

Findings tend to be right in direction and too strong in claim. Test the claim, not the direction.

- "Every X fails" -> usually some X; the rest are disabled, skipped, or commented out.
- "This breaks at runtime" -> often the DI container or the serializer already tolerates it.
- "Nothing validates this" -> often something bespoke does, just not the obvious mechanism.

When the strong version dies and a weaker one survives, report the weaker one **and** say the
strong version died. That correction is worth more to the reader than the finding.

## Honesty check (before writing the report)

- Did anything die? A pass that kills nothing was probably not a pass - an empty Dropped
  section usually means the pass was not honest.
- Did you correct any earlier claim of your own? Put it in Dropped.
- Is any surviving finding resting on a reference you did not open yourself?
- For each finding, could you answer "how do you know?" with a command or a `file:line`, not an
  argument?
