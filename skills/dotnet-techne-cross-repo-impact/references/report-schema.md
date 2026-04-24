# Report Schema

Exact markdown format for `cross-repo-impact.md`. Referenced by step 7
of `SKILL.md`.

## Filename and location

- Default filename: `cross-repo-impact.md`
- If the agent has an inline-comment capability: post as a PR review
  comment body, no file written.
- Otherwise: write to the workspace root.

## Required sections (in order)

```markdown
# Cross-repo impact — PR <org>/<repo>#<number>

*<agent-name or generator-name>* · `<ISO-8601 timestamp>` · base `<short-sha>` → head `<short-sha>`

## Summary

- **Overall severity:** `<Critical | High | Medium | Low | Unknown>` → **recommend `<BLOCK | REQUIRE-COORDINATED-PR | FLAG | OK>`**
- Findings: Critical `<n>` · High `<n>` · Medium `<n>` · Low `<n>` · Unknown `<n>`
- Affected downstream repos: `<n>`
- Compatible downstream PRs found: `<n>` / `<total affected repos>`
- Internal-only changes counted: `<n>` (not reported here; see standard review)

## Findings

### 1. <Symbol or endpoint or entity> — <Severity>

- **Kind:** `<kind>`
- **Change:** `<one-line before → after>`
- **Source:** `<path/from/repo>/<file>:<line>`
- **Affected repos:** `<repo-b>`, `<repo-c>`, ...
- **Callers (top 5, by certainty):**
  - `<repo>/<path>/<file>:<line>` — `<Exact | Inferred | Ambiguous>`
  - ...
  - *(and `<N>` more — truncated for brevity)*
- **Constraints violated:** `<plain text>`
- **Compatible PR:** `<url>` OR `NONE FOUND` OR `UNKNOWN (search failed)`
- **Confidence:** `<High | Medium | Low | Unknown>`
- **Warnings:** `<comma-separated list, or "none">`
- **Recommendation:** `<BLOCK | REQUIRE-COORDINATED-PR | FLAG | OK>`

### 2. ...
```

## Finding block fields

### `Kind`

One of:

- `ApiSignatureChange`
- `DtoShapeChange`
- `EndpointRouteChange`
- `EndpointVerbChange`
- `EntityColumnChange`
- `TableRename`
- `NugetVersionBump`
- `SerializationContractChange`

Value must come from the classifier output. Do not invent new kinds.

### `Change`

A single line of the form `<before> → <after>`. Examples:

- `public string Name → public string? Name`
- `route "/orders/{id}" → "/v2/orders/{id}"`
- `column Orders.CustomerId INT NOT NULL → INT NULL`
- `package Shared.Dtos 2.3.1 → 3.0.0`
- `[JsonPropertyName("customerId")] → [JsonPropertyName("customer_id")]`

Keep to one line. If the change is structurally complex, summarise and
cite the source location; do not inline a multi-line diff.

### `Source`

File and line from the PR repo where the change was made. Path is
relative to the repo root, not the workspace root.

### `Affected repos`

Comma-separated list of downstream repository names (basename, not full
path). Deduplicated. Ordered alphabetically.

### `Callers`

Up to 5 entries, ordered by certainty (Exact first), then by repo, then
by file path. Each entry has the form:

```
<repo>/<relative-file-path>:<line> — <Certainty>
```

If there are more than 5, append:

```
*(and <N> more — truncated for brevity)*
```

Never omit the truncation note — the reader must know there are more.

### `Constraints violated`

Plain-text explanation of what contract the change breaks. Examples:

- "Non-nullable DTO field removed; msgpack consumers will throw."
- "Route template changed; svc-b's HttpClient still targets the old path."
- "Column removed; svc-c has a pending migration against the old schema."

One to three short sentences. Agent-written; not from the classifier.

### `Compatible PR`

Exactly one of:

- A URL to an open PR/MR that matches the compat-PR search.
- `NONE FOUND` — search executed successfully and returned no matches.
- `UNKNOWN (search failed)` — search tool unavailable or errored.

### `Confidence`

From the confidence mapping table in `SKILL.md`. When multiple
certainties contribute, use the **lowest** (weakest-link) value.

### `Warnings`

Comma-separated list of short machine-readable tokens. Standard tokens:

| Token | Meaning |
|---|---|
| `ambiguous-evidence` | All upstream callers were Ambiguous or Unresolved. |
| `search-failed` | Compat-PR search errored; UNKNOWN recorded. |
| `in-fleet-package` | Bumped package is produced by another repo in the fleet. |
| `base-ref-unreachable` | `git checkout <base>` failed; used `merge-base` fallback. |
| `db-migration-missing` | EF change with no matching migration file. |
| `msgpack-consumer-risk` | Serialization change affects msgpack wire format. |

Add new tokens sparingly; document them here when introduced.

If no warnings, write `none`.

### `Recommendation`

One of `BLOCK`, `REQUIRE-COORDINATED-PR`, `FLAG`, `OK`. Derived from
severity per the rubric. Never override without recording a rationale in
`Constraints violated`.

## Truncation rules

Some PRs have many findings. Truncation rules:

- At most **20 findings** rendered in full detail.
- Findings 21+ rendered as a terse table at the bottom:

  ```markdown
  ## Additional findings (truncated)

  | # | Kind | Symbol | Severity | Rec |
  |---|---|---|---|---|
  | 21 | DtoShapeChange | ... | Medium | FLAG |
  | 22 | ... | ... | ... | ... |
  ```

- Overall severity still counts every finding, not just the top 20.
- If the rendered markdown exceeds ~60 KB (GitHub inline comment limit
  is 65 KB), split across multiple comments with `(... continued in next
  comment)` footers.

## Empty-report case

When `breaking_diff` finds no cross-repo impact, do not render the full
report. Instead, emit a single-line summary (as PR comment or workspace
file):

```markdown
**Cross-repo impact — PR <org>/<repo>#<n>:** no cross-repo-relevant
breaking changes detected · base `<sha>` → head `<sha>` · `<n>`
internal-only changes counted.
```

This tells the reviewer the check ran and passed, not "check was
skipped."

## JSON sidecar

Always emit the structured summary JSON (see `SKILL.md` step 7) alongside
the markdown. Tools downstream of the skill (chat adapters, dashboards)
consume the JSON; humans consume the markdown.

## Report metadata comment

Embed a machine-parseable marker at the top of the markdown so downstream
tooling can find the section:

```markdown
<!-- cross-repo-impact-report v1 -->
```

This lets edits/re-runs replace the existing comment rather than posting
duplicates.

## Example (abbreviated)

```markdown
<!-- cross-repo-impact-report v1 -->

# Cross-repo impact — PR myorg/svc-orders#142

*cross-repo-impact-skill* · `2026-04-24T12:00:00Z` · base `a1b2c3d` → head `e4f5g6h`

## Summary

- **Overall severity:** `Critical` → **recommend `BLOCK`**
- Findings: Critical `1` · High `0` · Medium `1` · Low `0` · Unknown `0`
- Affected downstream repos: `2`
- Compatible downstream PRs found: `1` / `2`
- Internal-only changes counted: `4`

## Findings

### 1. OrderDto.CustomerId — Critical

- **Kind:** `DtoShapeChange`
- **Change:** `public int CustomerId → removed`
- **Source:** `src/Orders/OrderDto.cs:14`
- **Affected repos:** `svc-billing`, `svc-notifications`
- **Callers (top 5, by certainty):**
  - `svc-billing/src/Clients/OrdersClient.cs:42` — `Exact`
  - `svc-notifications/src/Handlers/OrderCreated.cs:28` — `Exact`
- **Constraints violated:** Non-nullable DTO field removed; svc-billing
  deserialises OrderDto via System.Text.Json with required fields, will
  throw JsonException on first event.
- **Compatible PR:** `NONE FOUND`
- **Confidence:** `High`
- **Warnings:** `none`
- **Recommendation:** `BLOCK`

### 2. Shared.Dtos 2.3.1 → 3.0.0 — Medium

- **Kind:** `NugetVersionBump`
- **Change:** `package Shared.Dtos 2.3.1 → 3.0.0`
- **Source:** `Directory.Packages.props:18`
- **Affected repos:** `svc-notifications`
- **Callers (top 5, by certainty):** *(package reference, not direct callers)*
- **Constraints violated:** Major version bump; svc-notifications pins 2.x.
- **Compatible PR:** `https://github.com/myorg/svc-notifications/pull/87`
- **Confidence:** `High`
- **Warnings:** `in-fleet-package`
- **Recommendation:** `FLAG`
```
