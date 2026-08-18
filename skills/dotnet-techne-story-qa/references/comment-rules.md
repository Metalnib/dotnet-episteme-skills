# Comment rules

What a code comment is for, and what gets flagged in QA. Host-repo conventions (CLAUDE.md, a
style guide) override these defaults - read them first and say which source you applied.

## Production code

- A comment states a **why** - a constraint, a rationale, a non-obvious gotcha the code cannot
  express. Never the *what* or the *how* of the next line; that is the code's job.
- Default is **no comment**. Flag comments that restate the code, narrate the edit ("now we...",
  "changed to...", "added X"), or talk to a reviewer ("this is correct because...") - they are
  noise the moment the change merges.
- One short line. A comment that needs a paragraph usually marks code that needs restructuring;
  flag the paragraph, suggest the restructure only when it is obvious.
- No ticket numbers in production-code comments - the rationale must stand on its own. The one
  accepted exception: pre-existing `TODO: <ticket url>` lines stay; new ones are a finding.
- Dead comments: commented-out code, and comments describing behavior that no longer exists
  (the stale-words check owns factual staleness; this check owns style and placement).

## Tests

- Longer comments are fine in unit/integration tests: a scenario/context block above a test is
  good practice, not a finding.
- Test **names** carry intent - a test whose body needs a comment to explain what it verifies
  usually has the wrong name. Flag the name, not the comment.
- A test that proves a bug fix must reference its ticket in a comment (e.g. `// YB-12628: ...`).
  A bug-fix story whose regression test carries no ticket reference is a finding.

## Severity

Comment-rule findings are **suggestion** severity. Escalate to **important** only when a
comment is factually wrong about the current behavior (that pairs with a stale-words finding) or
when a required bug-fix ticket reference is missing.

## Fixing

QA workers never edit. The orchestrator may offer to apply comment-line fixes (delete noise
comments, trim to one why-line, add the missing ticket reference) after the report - and applies
them ONLY after the user explicitly confirms, as a separate step.
