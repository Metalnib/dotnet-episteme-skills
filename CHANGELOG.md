# Changelog

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
