# Changelog

## [1.8.0] — 2026-08-17

### Refactor pipeline (new)
- `/dotnet-refactor` runs a phase-gated design loop. Session-blind workers (`refactor:cartographer`, `refactor:tracer`) build the branch/consumer map and walk every dataflow path before any design exists, an approval gate guards implementation, and `refactor:conformance-auditor` re-checks the whole branch after any design change. `workflows/dotnet-refactor.js` fans the phases out and keeps raw worker output out of the conversation.
- `--lite` runs one combined `refactor:surveyor` on small targets, and escalates instead of skimming when the area turns out too big.
- State lives in `.episteme/DESIGN-<slug>.md`: status-routed frontmatter, the approved design frozen in a fence, out-of-scope findings in an append-only `Deferred` ledger. `hooks/design-state-reload.sh` re-injects it on startup, compact, clear and resume; PreCompact hooks flush unpersisted state first.

### Story QA pipeline (new)
- `/dotnet-qa` verifies a story against its spec: per-AC verdicts (IMPLEMENTED/PARTIAL/MISSING) with file:line evidence and the proving test, reuse and design conformance, dead code, stale words. `/dotnet-review` keeps the defect-hunting lane; the two are complementary.
- Spec discovery is a cascade: `--spec` → ticket key from the argument or branch name → repo artifacts → ask the user. Skipping the spec is an explicit choice and the dropped acceptance lane is announced. The spec pack carries ticket comments, issue links and the contract source of truth as content, because read-only workers cannot dereference a URL; a repo-local QA skill's rules fold in and win on conflict.
- Three session-blind lanes (`qa:acceptance`, `qa:reuse-design`, `qa:dead-code`) plus `review:maintainer`; the orchestrator runs the build and tests, and `workflows/dotnet-qa.js` computes a FAIL/CONCERNS/PASS gate into `.episteme/QA-<slug>.md`. All lanes inherit the session model - no runtime tier sizing (`--model` stays as the explicit override).
- The maintainer runs even on an all-IMPLEMENTED table, can upgrade a finding that got worse, and disputes AC verdicts whose evidence does not hold. A maintainer that fails to run gates CONCERNS instead of passing unfalsified findings.
- The dead-code lane checks comments against `references/comment-rules.md` and carries the exact replacement line; those fixes are applied only after explicit confirmation, the one write this flow offers.
- The report carries a verdict line, an owner tag per finding, a `Dropped` section for what falsification killed, a `Checked` coverage table and a `Yours to call` close. New portable skill `dotnet-techne-story-qa` runs the same QA in a single context on hosts without subagents.

### Cross-tool parity
- OpenCode registers all thirteen worker lanes and the three commands; the v2 module and the strong-tier variants mirror it.
- Codex gets `dotnet-techne-qa-pipeline` and `dotnet-techne-refactor-pipeline`; `scripts/install-codex.sh` registers all thirteen roles read-only.
- The bundled workflows register as `dotnet-review-workers`, `dotnet-qa-workers` and `dotnet-refactor-workers`, each described as "not a command", so pickers no longer show twins of the three commands (which launch them by scriptPath, unaffected).
- `docs/using-the-pipelines.md` walks through all three pipelines in plain words.

### Hardening
- The git guard covers the refactor and qa lanes under both Claude and Codex names; unrelated user roles with the same prefixes stay untouched. `rev-parse` and `merge-base` join the allowlist. `git diff --no-index` is denied everywhere, since it turns diff into a generic reader of arbitrary files.
- Refactor lanes get a wider read-only shell than the review lanes: rg/fd with grep/find fallbacks, plus ls, cat, head, tail, wc and tree. Each tool's own write and exec flags are denied (clustered short forms included), reads stay inside the project, and shell operators stay blocked. Full contract: `docs/reviewer-restrictions.md`.
- `scripts/validate.sh` enforces the worker-restriction frontmatter on the new lanes, keeps the `.episteme/DESIGN-` convention in sync between hook and command, and validates all three OpenCode command templates. `scripts/test-guard.sh` and `scripts/test-opencode-plugin.mjs` cover the new lanes and commands.

## [1.7.0] — 2026-07-27

### OpenCode plugin (new)
- `opencode/dotnet-episteme.js` registers the 10 skills, the five reviewers plus the adversarial maintainer as `review-*` subagents, `/dotnet-review`, and the Synopsis MCP server. Reviewer prompts come from `agents/review/*.md`, so the lanes stay single-sourced across tools.
- Reviewers are read-only and exfil-safe: `edit`, `webfetch`, `websearch`, and `task` denied; bash limited to read-only git with shell operators and output flags blocked by trailing deny rules (permission resolution is last-match-wins); reads outside the project allowed only for the plugin's own checklists.
- `DOTNET_EPISTEME_STRONG_MODEL` (or plugin option `strongModel`) registers pinned `review-<lane>-strong` variants, because OpenCode's `task` tool takes no per-call model.
- `scripts/install-opencode.sh` / `.ps1`: symlink install, Synopsis pre-warm, CLI verification, `--verify` / `--uninstall`. On Windows without Developer Mode the fallback is a re-export shim (a plain copy resolves the plugin's root wrong and registers nothing; a copied file now degrades with instructions instead of throwing).
- `opencode/dotnet-episteme.v2.js` — dormant module for OpenCode's beta v2 plugin API, linked via `scripts/install-opencode.sh --v2`; the stable v1 module stays the default.

### Codex plugin (new)
- `.codex-plugin/plugin.json` and `codex/mcp.json`: `codex plugin add` installs the skills, the Synopsis MCP server, and the read-only git guard. The launcher lookup honours `CODEX_HOME` and reports a clear error when no installed copy is found.
- `codex/skills/dotnet-techne-review-pipeline` drives the multi-agent review as a skill — Codex custom prompts are deprecated in favour of skills.
- `scripts/install-codex.sh` registers the six `review-*` roles with `sandbox_mode = "read-only"` and raises `agents.max_concurrent_threads_per_session` to 6 so the lanes run in parallel. Idempotent, `--verify` / `--uninstall`.
- The pipeline states its six-agent-turn cost before fanning out and offers the single-context review skill for small diffs.
- `hooks/git-readonly-guard.sh` also guards the Codex reviewer roles: Codex subagent `PreToolUse` payloads carry `agent_type`, so the guard scopes to the `review-*` lanes there too (project boundary from the payload `cwd`). One posture across tools: [docs/reviewer-restrictions.md](docs/reviewer-restrictions.md).

### Packaging
- `package.json` packages the OpenCode plugin; a tag-gated `publish-npm` job publishes it once an `NPM_TOKEN` secret exists, and skips without one.
- The release archive carries `.codex-plugin/`, `codex/`, `opencode/`, `hooks/` and `package.json`.
- MIT `LICENSE`.

### Fixes
- Reviewer restrictions are now enforced on every lane, not just documented: `agents/review/data-messaging.md` denies `Task`, `WebFetch` and `WebSearch` alongside writes (it deny-lists rather than allow-lists so the Synopsis MCP tools stay visible), so no reviewer can reach the network or spawn a nested agent. `scripts/validate.sh` checks each lane, whichever style it uses.
- `scripts/validate.sh`: workflow scripts are parsed as an async function body, so `node --check` no longer rejects `workflows/dotnet-review.js`. New checks cover the Codex manifest and version drift across `plugin.json`, `.codex-plugin/plugin.json` and `package.json`.
- Release notes counted skill roots, so they reported 1 skill instead of 10.

## [1.6.0] — 2026-07-24

### Claude Code plugin
- **Multi-agent code review** — `/dotnet-episteme-skills:dotnet-review` runs five read-only reviewers in parallel (correctness, performance, security/observability, data/messaging, generalist) plus an adversarial maintainer that re-verifies each finding with `file:line` evidence; reviewer model scales with change size (`--model` overrides).
- **`plugin.json`** — dropped the empty `"agents": []` key that hid the new agents.

### Synopsis
- **MCP auto-start** — declared in the plugin `.mcp.json`; tools appear as `mcp__plugin_dotnet-episteme-skills_synopsis__*`, state under the plugin data dir. macOS/Linux, or Windows under WSL2.
- **`schemaVersion`** — on every `--json` envelope and the MCP `initialize` handshake (`capabilities.experimental.synopsis`, mirrored in `serverInfo`); envelope string values are JSON-escaped.
- **`--log-file <path>`** (env `SYNOPSIS_LOG_FILE`) — `synopsis mcp` also appends diagnostics to a file.
- **Async startup scan** — `synopsis mcp --root` answers `initialize` immediately and scans in the background; `scan_stats` reports indexing status.
- **Experimental log monitor** — `monitors/monitors.json`, interactive Claude Code only.
- **Slim binaries** — framework-dependent `synopsis-<RID>-slim` archives alongside self-contained.

### Scanning fixes
- **`.slnx` discovery (#9)** — recognise the .NET 9/10 default solution format; a directory with both `X.sln` and `X.slnx` keeps only the `.slnx`.
- **Transitive projects (#10)** — reused instead of dropped with "already part of the workspace".
- **`--exclude` in solution mode (#8)** — now filters solution-loaded projects, not just filesystem discovery.

### Dependencies
- **Security refresh** — `System.Security.Cryptography.Xml` 10.0.6 → 10.0.10 (clears NuGet advisories); Roslyn 5.6.0, MSBuild 18.8.2, test tooling latest. Build green with audit on.

### Docs and packaging
- **`docs/`** — refactor plan and per-tool compatibility matrix; skills stay plain Agent Skills folders.
- **`scripts/validate.sh`** — covers agents, commands, `.mcp.json`, and monitors.
- **Version 1.6.0** — plugin, marketplace, binary, and skill metadata.

## [1.5.0] — 2026-04-24

### New MCP tools (Synopsis)
- **`endpoint_callers`** — find every caller of an HTTP endpoint across repos, with resolution certainty and resolved target IDs.
- **`package_dependents`** — list all repos/projects that depend on a NuGet package, with per-project version and optional exact-version filter.
- **`table_entry_points`** — trace upstream from a database table through EF Core lineage and call edges to surface the HTTP endpoints that write or read it.
- **`repo_dependency_matrix`** — service-to-service HTTP call dependency map: outbound call counts per repo and resolved cross-repo dependency pairs.

### Improvements
- **Symlink sandbox** — `reindex_repository` now resolves symlinks before validating the path against the workspace root, preventing a symlink-escape bypass.
- **Warning deduplication** — workspace partitioning now routes scan warnings to their owning repository instead of broadcasting every warning to every repo.
- **Platform-aware path comparison** — `Paths.FileSystemComparer`/`FileSystemComparison` use `Ordinal` on Linux and `OrdinalIgnoreCase` on macOS/Windows, matching actual filesystem behaviour.
- **`synopsis --version`** — CLI now prints the version sourced from `Directory.Build.props` (single source of truth for all projects).
- **MCP `initialize` version** — `serverInfo.version` now reads from the assembly attribute instead of a hardcoded `"1.0.0"`.

### Fixes
- Cross-repo dependency matrix double-counted each resolved HTTP call; now counts only `CrossesRepoBoundary` edges.
- `reindex_repository` path sandbox used case-insensitive comparison on Linux.
- Merge iteration order in `CombinedGraph.RebuildAndPublish` used `OrdinalIgnoreCase` sort, breaking byte-stability on Linux case-sensitive filesystems.
- Dead `connCts.Cancel()` call removed from MCP server connection teardown.

### Tests
- 19 new tests covering all four analysis tools and `ResolveAllowedPath` acceptance/rejection paths.

---

## [1.4.0] — 2026-04-15

### New features (Synopsis)
- **Breaking-change classifier** (`synopsis breaking-diff`, `breaking_diff` MCP tool) — typed `BreakingChangeKind` values with severity and certainty.
- **Daemon mode** (`synopsis mcp --socket` / `--tcp`) — persistent multi-repo `CombinedGraph` with Unix socket and TCP transports.
- **NuGet graph nodes** — `Package` node type and `DependsOnPackage` edges; CPM and inline version sources tracked per edge.
- **State persistence** — `JsonFileStateStore` backs cold-start recovery; `reindex_repository` / `reindex_all` MCP tools for incremental re-scan.
- **Multi-arch binaries** — pre-built for osx-arm64, osx-x64, win-x64, win-arm64, linux-x64, linux-arm64.

---

## [1.3.3] — 2026-03-20

- Initial public release of `dotnet-techne-synopsis` skill.
- MCP tools: `blast_radius`, `find_paths`, `list_endpoints`, `list_nodes`, `node_detail`, `db_lineage`, `cross_repo_edges`, `ambiguous_review`, `scan_stats`.
- CLI commands: `scan`, `watch`, `export`, `query`, `git-scan`, `diff`.
