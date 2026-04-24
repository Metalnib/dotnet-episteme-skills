# Severity Rubric

Full rules for assigning severity to a cross-repo breaking-change finding.
Referenced by step 6 of `SKILL.md`.

## Base severity (from the classifier)

Synopsis's `breaking_diff` command emits a base severity per change kind:

| Change kind | Base severity | Rationale |
|---|---|---|
| `EndpointRouteChange` | Critical | Clients calling the old route get 404 at runtime. |
| `EndpointVerbChange` | Critical | Clients using the old verb get 405; rarely caught in tests. |
| `DtoShapeChange` (field removed, type narrowed, required-ness tightened) | Critical | Serialisation fails; consumers see partial/null data or hard errors. |
| `EntityColumnChange` (column removed, type narrowed, non-null tightened) | Critical | EF throws at runtime; requires coordinated migration. |
| `TableRename` | Critical | Raw SQL + EF mapping mismatch; data may survive but queries fail. |
| `SerializationContractChange` (`[JsonPropertyName]`, `[DataMember]`, converter swap) | Critical | Wire format changes silently for JSON/msgpack consumers. |
| `NugetVersionBump` (shared library, major version change) | High | Depends on library's own break discipline; assume breaking by default. |
| `NugetVersionBump` (minor/patch of shared library) | Low | Usually safe; surfaced for awareness. |
| `ApiSignatureChange` (public method/record signature diff) | High | Compile-time break downstream; caught at build but still disruptive. |
| `DtoShapeChange` (field added, nullable widening, default added) | Low | Usually backward-compatible; surfaced for awareness. |
| `EndpointRemoved` | Critical | Clients get 404. Most breaking "deletion" case. |
| `TableRemoved` | Critical | Queries fail; data may need recovery. |
| `ApiRemoved` | High | Compile-time break for every caller — dependent code will not link. |
| `PackageRemoved` | Low | Usually intentional, done after removing all usage. Downstream skill escalates if unexpected. |

The classifier's output carries these base severities. Do not recompute
them from the change kind alone — the classifier considers certainty and
edge metadata that the kind name does not capture.

## Transforms applied in step 6

Applied in order. Severity is a monotonic escalation — never
down-escalated.

### Rule 1 — No compatible PR → escalate one level

Applied per affected downstream repo. If no compatible PR is found and
the base severity is X, bump to X+1:

- Low → Medium
- Medium → High
- High → Critical
- Critical → Critical (already max)

**Rationale.** A breaking change without a coordinated downstream fix
will break that service at deploy time. This is the whole point of the
analysis.

### Rule 2 — Compat-PR search returned UNKNOWN → escalate + warn

Treat UNKNOWN (search CLI missing, rate-limited, errored) as "no
compatible PR" for severity purposes. Add warning `search-failed: <repo>`.

**Rationale.** We do not have positive evidence of a fix. Defaulting to
"no PR" errs on the safe side without silently hiding the uncertainty.

### Rule 3 — All evidence ambiguous → cap at Medium

If every upstream caller for a finding has certainty `Ambiguous` or
`Unresolved`, cap the finding's severity at Medium regardless of
classifier severity. Add warning `ambiguous-evidence`.

**Rationale.** We cannot confidently claim any specific caller breaks.
A Critical claim backed only by ambiguous edges is a noise signal; a
Medium flag invites manual inspection without blocking.

### Rule 4 — Synopsis-unavailable degraded mode → Unknown

If Synopsis could not produce a graph at all (unreachable daemon,
binary missing, scan failed), emit every finding as `severity: Unknown`
and recommendation `FLAG`. Do not invent severities.

## Mapping severity to recommendation

After transforms, each finding gets a recommendation:

| Severity | Default recommendation |
|---|---|
| Critical | `BLOCK` |
| High | `REQUIRE-COORDINATED-PR` |
| Medium | `FLAG` |
| Low | `OK` (note only) |
| Unknown | `FLAG` (with reason) |

Recommendation is advisory. The skill does not enforce; code-host gates
(required reviewers, CI status checks) do.

## Overall PR severity

The PR's overall severity is the **highest** individual finding.

Summary-line recommendation follows from that:

- Any Critical → PR recommendation `BLOCK`
- Any High (no Critical) → `REQUIRE-COORDINATED-PR`
- Any Medium (no High/Critical) → `FLAG`
- All Low → `OK`
- Only Unknowns → `FLAG`

## Edge cases

### Multiple change kinds on the same symbol

If one symbol has both an `ApiSignatureChange` and a
`SerializationContractChange`, emit two findings, each with its own base
severity. Transforms apply per finding. The summary shows the higher.

### Same downstream caller hit by multiple findings

Do not double-count. The caller appears in each finding's caller list
but the "affected repos" counter dedupes at repo level.

### Compat-PR covers only part of the impact

If a downstream PR fixes one affected symbol but not another, treat
`compatiblePr` as found for the covered finding only; the uncovered
finding escalates per Rule 1.

### Shared library bump where the library is also in-fleet

If the bumped package is produced by another repo in the fleet, add
warning `in-fleet-package` and treat the package version bump as a
transitive signal: walk its API changes via Synopsis and emit findings
per API change rather than one generic `NugetVersionBump`.

### Auto-generated code / source-generated partials

Ignore breaking changes in files matching common source-gen paths
(`obj/**`, `**/*.g.cs`, `**/*.Designer.cs`). Synopsis should mark these
but the skill double-checks before reporting.

## Reference: severity-to-emoji map (for chat adapters)

Not part of the canonical report; optional for rich chat rendering:

| Severity | Emoji |
|---|---|
| Critical | 🛑 |
| High | ⚠️ |
| Medium | 🔸 |
| Low | · |
| Unknown | ❔ |
