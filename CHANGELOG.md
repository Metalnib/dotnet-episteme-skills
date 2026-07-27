# Changelog

## [1.7.0] — 2026-07-27

### OpenCode plugin (new)
- `opencode/dotnet-episteme.js` registers the 10 skills, the five reviewers plus the adversarial maintainer as `review-*` subagents, `/dotnet-review`, and the Synopsis MCP server. Reviewer prompts come from `agents/review/*.md`, so the lanes stay single-sourced across tools.
- Reviewers are read-only: `edit` and `webfetch` denied, bash limited to read-only git.
- `DOTNET_EPISTEME_STRONG_MODEL` (or plugin option `strongModel`) registers pinned `review-<lane>-strong` variants, because OpenCode's `task` tool takes no per-call model.
- `scripts/install-opencode.sh` / `.ps1`: symlink install, Synopsis pre-warm, CLI verification, `--verify` / `--uninstall`.

### Codex plugin (new)
- `.codex-plugin/plugin.json` and `codex/mcp.json`: `codex plugin add` installs the skills, the Synopsis MCP server, and the read-only git guard.
- `codex/skills/dotnet-techne-review-pipeline` drives the multi-agent review, since Codex has no custom slash commands.
- `scripts/install-codex.sh` registers the six `review-*` roles with `sandbox_mode = "read-only"` and raises `agents.max_concurrent_threads_per_session` to 6 so the lanes run in parallel. Idempotent, `--verify` / `--uninstall`.
- The pipeline states its six-agent-turn cost before fanning out and offers the single-context review skill for small diffs.
- `hooks/git-readonly-guard.sh` stays out of the way on Codex, whose `PreToolUse` payload carries no `agent_type`.

### Packaging
- `package.json` packages the OpenCode plugin; a tag-gated `publish-npm` job publishes it once an `NPM_TOKEN` secret exists, and skips without one.
- The release archive carries `.codex-plugin/`, `codex/`, `opencode/`, `hooks/` and `package.json`.
- MIT `LICENSE`.

### Fixes
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
