# Changelog

## [Unreleased]

### OpenCode plugin (new)
- **`opencode/dotnet-episteme.js`** — a native OpenCode plugin whose `config` hook registers what the Claude Code plugin registers: the skills path, the five reviewers plus the adversarial maintainer as `review-*` subagents, the `/dotnet-review` command, and the Synopsis MCP server. Reviewer prompts are read from `agents/review/*.md` at load time, so those files stay the single source of truth; their Claude frontmatter is discarded, because a comma-string `tools:` key fails OpenCode's schema and takes down config resolution for the whole session.
- **Read-only reviewer sandbox** — registered as per-agent `permission` rules (`edit`/`webfetch` denied, bash restricted to `git diff|log|show|blame|status|rev-parse|merge-base`), replacing `hooks/git-readonly-guard.sh` on OpenCode. Stricter in one respect: the allow patterns anchor on the subcommand, so `git -C <other repo> diff` matches no allow rule, and OpenCode parses chained commands, so `git status && rm -rf x` is denied.
- **`opencode/dotnet-review.template.md`** — the orchestrator adapted to OpenCode: no `Workflow` step (no workflow runtime exists), `{{PLUGIN_ROOT}}` resolved at load time (OpenCode does not expand variables in command templates), flat `review-*` subagent names, and tier guidance that matches the runtime.
- **Optional model tiering** — OpenCode's `task` tool has no per-invocation `model`, so `DOTNET_EPISTEME_STRONG_MODEL` (or plugin option `strongModel`) registers a pinned `review-<lane>-strong` variant of every lane and the command is told to dispatch those when the sizing table calls for a stronger tier.
- **`scripts/install-opencode.sh` / `.ps1`** — symlink install (so `git pull` is the update path), a warning when a hand-copied Claude agent file is found in OpenCode's agent directory, Synopsis binary pre-warm (a fresh clone has no `bin/`, and OpenCode's MCP startup window is too short to download one), and CLI verification of all four registrations. `--verify` / `--uninstall` supported.
- **`docs/opencode-setup.md`** — install, verification, the optional strong tier, the v1/v2 config-shape traps (one v2-shaped key in a v1 document silently discards the whole file), and troubleshooting.

### Codex plugin (new)
- **`.codex-plugin/plugin.json`** — a spec-compliant Codex manifest (kebab-case name, semver, `author.name`, `interface` block; no `hooks` field, which Codex validation rejects). `codex plugin marketplace add Metalnib/dotnet-episteme-skills` + `codex plugin add dotnet-episteme-skills@dotnet-episteme-marketplace` then installs the skills, the Synopsis MCP server, and the read-only git guard. Verified on codex-cli 0.145.0.
- **`codex/mcp.json`** — fixes the MCP server on Codex. Codex stores an MCP command literally and spawns it directly, so `${CLAUDE_PLUGIN_ROOT}` produced `MCP startup failed: No such file or directory`; plugin-spawned MCP processes also receive no `PLUGIN_ROOT`/`CLAUDE_PLUGIN_ROOT` (only hooks do). The entry now resolves the shared launcher from `$HOME` through a shell, newest cache entry first, and sets `startup_timeout_sec = 120` so a first-run binary download cannot blow the startup window.
- **`codex/skills/dotnet-techne-review-pipeline`** — the orchestrator as a skill, because Codex has no custom slash commands (`~/.codex/prompts` is not a feature of 0.145.0). It lives in a second skills root declared by the Codex manifest, so Claude Code and OpenCode never see a competing review entry point.
- **`scripts/install-codex.sh`** — registers the six `review-*` roles in `config.toml` (plugins cannot ship roles), each with a TOML layer whose `model_instructions_file` points at a frontmatter-stripped copy of the same `agents/review/*.md` body the other two tools use, and `sandbox_mode = "read-only"` for enforcement. Idempotent through a marked block, with `--verify` / `--uninstall` and Synopsis binary pre-warm.
- **Concurrency and cost** — `install-codex.sh` sets `agents.max_concurrent_threads_per_session = 6` (skipped, with a printed note, when you configure `[agents]` yourself), because Codex's default cap makes the five "parallel" reviewers run in waves of three. The pipeline skill now states the six-agent-turn cost before fanning out and offers the single-context review skill for small diffs — a two-commit review was enough to exhaust a free monthly allowance in testing.
- **`hooks/git-readonly-guard.sh`** — exits early when the payload has no `agent_type`. Codex sends it only on `SubagentStart`/`SubagentStop`, never on `PreToolUse`, so the guard has no reviewer to scope to there and now says so explicitly instead of spawning `python3` on every command.

### Packaging
- **npm package `opencode-dotnet-episteme`** — `package.json` publishes the OpenCode plugin with the skills, reviewer prompts, and Synopsis launcher (99 files; platform binaries stay auto-downloaded), so installing is `opencode plugin opencode-dotnet-episteme -g`. This is also the only path that can configure the plugin: options reach plugins declared in config, not plugins dropped into the plugin directory as files, so `{ "strongModel": "…" }` works here while the file install needs the env var.
- **Release automation** — a tag-gated `publish-npm` job verifies tag/`package.json` alignment, re-runs the registration test, and publishes with provenance; it skips cleanly until the `NPM_TOKEN` secret exists. The skills archive now also carries `opencode/`, `hooks/`, and `package.json`.
- **`scripts/validate.sh`** — fails when `package.json` and `.claude-plugin/plugin.json` versions drift, or when `main` points at a missing file.

### Fixes
- **`scripts/validate.sh` workflow check** — `node --check` rejected `workflows/dotnet-review.js` because workflow scripts execute as an async function body, where top-level `return`/`await` are legal; the check now parses them the way the runtime does. This failure made validation red on `main`.

### Docs
- **`docs/tool-compatibility.md`** — recorded that Codex installs this repo as a plugin with no Codex-specific files (verified on codex-cli 0.145.0: marketplace add, plugin add, 10 skills in the model prompt, `.mcp.json` read), plus the two gaps blocking the review pipeline there (`${CLAUDE_PLUGIN_ROOT}` unexpanded in MCP commands; `PreToolUse` carries no `agent_type`, so the read-only guard cannot scope itself to reviewers).
- **`docs/tool-compatibility.md`** — corrected two false OpenCode rows (multi-subagent review and maintainer pushback are supported there, not "single-context only"), added rows for the reviewer sandbox and per-reviewer model tier, and recorded the contributor rule that `agents/review/*.md` stay Claude-shaped with the plugin converting them.
- **`README.md`** — OpenCode install path; the skills count in the install table said 9, not 10.

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
