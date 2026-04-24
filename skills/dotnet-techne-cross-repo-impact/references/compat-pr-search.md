# Compatible-PR Search

Heuristics and CLI recipes for step 5 of `SKILL.md`: finding an open PR
in a downstream repo that likely fixes a breaking change introduced by
the PR under review.

## What counts as "compatible"

A compatible PR is one that — if merged alongside the PR under review —
would prevent the downstream repo from breaking. Typical examples:

- Updates an HTTP client to use the new endpoint route.
- Regenerates DTO bindings to match the new shape.
- Adds an EF migration matching the new column schema.
- Bumps the shared NuGet package to the new version.
- Updates a route constant or configuration key.

## Signal priority

Use these signals in order; stop at the first positive match. Record
which signal matched in the finding's warnings or log output.

1. **Linked issue / ticket number** — if the PR under review mentions
   issue `#N` or ticket `PROJ-1234`, search downstream repos for the
   same reference. Highest signal.
2. **Exact symbol match** — the changed symbol's display name in PR
   title or body.
3. **Branch-name pattern** — conventions used in your org
   (`fix/*<symbol>*`, `chore/sync-<pr-number>`, `bump/<package>`, etc.).
4. **Freshness** — restrict to open PRs opened within the last 30 days,
   or since the PR under review was opened (whichever is longer).

Avoid weak signals that cause false positives:

- Author match alone (same person could be doing unrelated work).
- Generic keywords like "fix", "update", "bump" without a symbol
  attached.
- Closed / merged PRs (we want *still open* fixes).

## GitHub (`gh`)

### By keyword in title or body

```bash
gh pr list \
  --repo "<owner>/<downstream-repo>" \
  --state open \
  --search "<symbol> in:title,body" \
  --json number,title,url,headRefName,updatedAt \
  --limit 20
```

### By linked issue

```bash
gh pr list \
  --repo "<owner>/<downstream-repo>" \
  --state open \
  --search "\"#<issue-number>\"" \
  --json number,title,url
```

Use the GraphQL closing-references API for strict "closes #N" matches if
available in your gh version:

```bash
gh api graphql -f query='
  query($owner: String!, $repo: String!, $issue: Int!) {
    repository(owner: $owner, name: $repo) {
      pullRequests(states: OPEN, first: 20, orderBy: {field: UPDATED_AT, direction: DESC}) {
        nodes {
          number url title
          closingIssuesReferences(first: 10) { nodes { number repository { nameWithOwner } } }
        }
      }
    }
  }' -f owner=<owner> -f repo=<downstream-repo> -F issue=<issue-number>
```

### By branch-name pattern

```bash
gh pr list \
  --repo "<owner>/<downstream-repo>" \
  --state open \
  --json number,title,url,headRefName \
  --jq '.[] | select(.headRefName | test("(?i)(fix|sync|bump).*<symbol>"))'
```

## GitLab (`glab`)

### By keyword

```bash
glab mr list \
  --repo "<group>/<downstream-repo>" \
  --state opened \
  --search "<symbol>" \
  --per-page 20
```

### By referenced issue

```bash
glab api "projects/<url-encoded-project>/merge_requests?state=opened&search=<issue-number>"
```

### By branch pattern

```bash
glab mr list --repo "<group>/<downstream-repo>" --state opened --output json \
  | jq '.[] | select(.source_branch | test("(?i)(fix|sync|bump).*<symbol>"))'
```

## Bitbucket Cloud (reference — no first-class MVP support)

```bash
curl -s -u "$BITBUCKET_USER:$BITBUCKET_APP_PASSWORD" \
  "https://api.bitbucket.org/2.0/repositories/<workspace>/<repo>/pullrequests?state=OPEN&q=title%20~%20%22<symbol>%22"
```

## Azure DevOps (reference)

```bash
az repos pr list \
  --repository "<repo>" \
  --status active \
  --query "[?contains(title, '<symbol>') || contains(description, '<symbol>')]"
```

## Rate-limit handling

- GitHub REST: 5000 req/hour authenticated; search API: 30 req/min. Prefer
  filtered list calls over search when possible.
- GitLab: varies by instance; default 600 req/min.
- On rate-limit error, mark the finding's `compatiblePr: UNKNOWN` and add
  warning `search-failed`; do not block the whole analysis.

## Symbol synonyms

The changed symbol's display name may not match downstream search
targets verbatim. Common synonyms to try:

| Symbol kind | Also try |
|---|---|
| DTO `OrderDto` | `Order`, `OrderModel`, `OrderPayload`, `OrderContract` |
| Endpoint `GetOrders` | route path (`/orders`), HTTP verb (`GET /orders`) |
| Entity `Orders` | `Order` (singular), table name (`orders`), column names |
| Package `Shared.Dtos` | `shared.dtos`, `Shared`, unique type from within the package |

For endpoint changes, always search for the **old route path** as a
literal — downstream fixes typically mention `/orders/{id}` in the PR
body to show what they're updating.

## Branch-naming conventions by org

Teams commonly use these patterns for coordinated-fix PRs:

- `fix/<ticket-id>`
- `sync/<upstream-repo>-<pr-number>`
- `bump/<package>-to-<version>`
- `upgrade/<lib>-<version>`
- `compat/<upstream-pr-number>`
- `chore/update-<symbol>`

If your org has a stricter convention, override the branch pattern in
the agent's configuration.

## False-positive filters

After collecting candidates, filter out:

- PRs marked as draft/WIP if the team treats those as not-ready.
- PRs whose diff stats are too small to include the needed fix (heuristic:
  `additions + deletions < 3`).
- PRs whose title contains `revert`, `rollback`, or matches the pattern
  of an autogenerated dependabot/renovate bump for an unrelated package.

Applying these filters is optional; when in doubt, include the candidate
and let the reviewer decide. Document any filter applied in the warnings
field.

## "No compat PR found" is itself informative

A negative result — "searched, found nothing matching" — carries real
weight: it tells the reviewer a Critical finding is likely to break
production on deploy. Always distinguish between:

- `NONE FOUND` (search executed, zero matches) — strong negative signal.
- `UNKNOWN` (search failed) — no signal either way; escalate conservatively.

Never conflate these.
