# Spec discovery cascade

Resolve the story's spec before any QA dimension runs. Stop at the first tier that produces a
usable spec; never skip to "no spec" on your own.

1. **Explicit**: a `--spec <path>` argument, a pasted spec/ticket text, or a spec file named in
   the request. Verify the file reads; warn about referenced docs that cannot be found.
2. **Ticket id**: from the argument or the current branch name - a Jira-style key matching
   `[A-Z][A-Z0-9]+-\d+` (e.g. `YB-13323` out of `feature/YB-13323-add-x`). If an issue-tracker
   MCP tool is available (Jira or similar), fetch the ticket: description, acceptance criteria,
   the comments, the issue links (blocked/blocking stories, the epic, sibling stories), and
   linked spec pages. Links matter: they set the scope boundary, expose backlog gaps, and are
   the only way to test the "another ticket covers it" counter-argument. No tracker tool
   available - continue the cascade using the key as a search term.
3. **Repo artifacts**: search the repository for the key or story name in
   `.episteme/DESIGN-*.md`, `PLAN*` files, `docs/`, and `.claude/docs/`. A design or plan
   document that states the intended behavior counts as a spec.
4. **Ask the user** - exactly two options, nothing else:
   1. provide a spec/ticket/plan reference, or
   2. continue without a spec.
   **No-spec mode is never inferred from a missing path; the user must explicitly choose it.**
   When chosen, the acceptance dimension is skipped and the report says so - degradation is
   announced, never silent.

Build the **spec pack** from whatever tier hit:

- Numbered acceptance criteria (number them yourself when the source is prose; keep the
  source's own numbering when it has one).
- Constraints (non-functional notes, compatibility requirements).
- Out-of-scope declarations.
- Claimed file/task lists when the source carries them.
- The source itself (ticket id/URL or file path) for the report header.
- Issue links and the relevant ticket comments, as short id + one-line summaries. The QA
  workers and the maintainer have no tracker access; a backlog-gap finding can only be
  falsified against links that travel in the pack.
- **Contract source of truth**: most stories are judged against something outside the repo -
  a consumer's test suite, a vendor schema, a WSDL, an OpenAPI document, a sibling service. The
  ticket usually names it. Capture its **content** in the pack - the relevant excerpts, or a
  path inside the reviewed repo - not just a URL or an outside path: the workers have no web
  access, and on some hosts cannot read files outside the repo, so a pointer they cannot
  dereference silently skips the check. Fetch it yourself at pack-build time. Include the
  consumer of the API, not just its schema - how the caller parses a response is what
  "success" really means. This is the artifact that separates "looks right" from "verified";
  hand-written names, wire casing, and envelope shapes drift exactly here, and only the source
  of truth catches it.

The spec pack is data for the QA workers: no conversation narration and no design rationale
from the session travel with it.
