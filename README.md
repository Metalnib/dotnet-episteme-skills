# DotNet Episteme Skills

Production-tested .NET skills for AI coding tools. Each skill gives your AI agent enterprise-proven instructions for implementing, reviewing, and improving .NET systems.

---

## Quick install (Claude Code)

```text
/plugin marketplace add Metalnib/dotnet-episteme-skills
```

Then open the plugin browser and install **dotnet-episteme-skills**. Restart Claude Code — skills are ready immediately.

> **Other tools:** See [Manual installation](#manual-installation) below.

---

## What's included

| Skill | What it does | When to invoke |
|---|---|---|
| `dotnet-techne-synopsis` | Static dependency & blast-radius explorer — scans your .NET repos, builds a full call/dependency graph, answers architectural questions | "What does changing X break?", "Show me all callers of this endpoint", "Map service-to-service HTTP calls" |
| `dotnet-techne-code-review` | End-to-end .NET PR review: correctness, performance, security, data, messaging, observability | Any PR or diff review |
| `dotnet-techne-crap-analysis` | CRAP score and coverage hotspot analysis | Prioritising where to add tests or reduce complexity |
| `dotnet-techne-csharp-api-design` | Compatibility-safe API evolution and deprecation strategy | Designing or changing public APIs |
| `dotnet-techne-csharp-coding-standards` | Modern C# idioms, refactoring guidance | Code quality questions, style inconsistencies |
| `dotnet-techne-csharp-concurrency-patterns` | Async, channels, dataflow, Rx — choosing the right pattern | Concurrency design or bug investigation |
| `dotnet-techne-csharp-type-design-performance` | Structs, sealed types, spans, frozen collections on hot paths | Performance-sensitive code |
| `dotnet-techne-serialisation` | Serialisation format and wire-compatibility decisions | API contracts, messaging, persistence |
| `dotnet-techne-inspect` | Inspect NuGet package APIs, decompile method signatures | Understanding a third-party package's surface area |

Skills are invoked automatically when relevant (keyword-triggered) or explicitly by typing `/dotnet-techne-<name>`.

---

## Getting started

Once installed, just describe what you want in plain language. Examples:

```
Review this PR for production readiness.

What's the blast radius if I change IOrderRepository?

Show me all HTTP endpoints that write to the Orders table.

Design a backwards-compatible version of this API.
```

Claude will select the right skill automatically. For Synopsis (the dependency analysis tool), it auto-downloads its binary on first use — no manual setup needed.

---

## Synopsis — dependency graph and blast-radius analysis

Synopsis is the most capable skill in this set. It uses Roslyn to scan your entire .NET workspace, build a semantic dependency graph, and answer questions AI agents can't answer from code reading alone.

### What it discovers

| Layer | What Synopsis extracts |
|---|---|
| **Workspace** | Repositories, solutions, projects, cross-repo boundaries |
| **Types** | Classes, interfaces, DI registrations, constructor injection |
| **Call graph** | Method-to-method calls, interface dispatch resolved via DI |
| **ASP.NET endpoints** | Controller actions, minimal API routes, route templates |
| **HTTP clients** | `HttpClient`/`IHttpClientFactory` calls, cross-service resolution |
| **EF Core lineage** | `DbContext` → `DbSet<T>` → entity → table, `ToTable()` mappings |
| **NuGet packages** | Package nodes with version, CPM vs inline, per-project dependency edges |
| **Certainty** | Every node and edge is tagged: Exact, Inferred, Ambiguous, or Unresolved |

### MCP tools (available to AI agents)

When running in MCP mode, Synopsis exposes these tools:

| Tool | What it answers |
|---|---|
| `blast_radius` | What does changing this symbol break? |
| `find_paths` | How does A connect to B? |
| `list_endpoints` | What HTTP endpoints exist, filtered by project or verb? |
| `node_detail` | Everything known about one node — all edges in and out |
| `db_lineage` | EF Core chain from DbContext down to the database table |
| `cross_repo_edges` | All calls that cross repository boundaries |
| `ambiguous_review` | Unresolved and ambiguous edge audit |
| `scan_stats` | Scan metadata and statistics |
| `breaking_diff` | Classify breaking changes between two graph snapshots |
| `endpoint_callers` | Every service that calls a given HTTP endpoint, with certainty |
| `package_dependents` | Which repos depend on a NuGet package, and at what version? |
| `table_entry_points` | Which HTTP endpoints eventually read or write a database table? |
| `repo_dependency_matrix` | Service-to-service HTTP call map across all repos |
| `list_repositories` | Repos tracked by the daemon with scan timestamps |
| `reindex_repository` | Re-scan one repo and update the live graph |
| `reindex_all` | Re-scan every tracked repo |

### CLI quick start

```bash
# Scan a workspace
synopsis scan /path/to/workspace -o graph.json

# Blast radius of a symbol
synopsis query symbol --fqn "OrdersController" --blast-radius --graph graph.json

# What calls into the Orders table?
synopsis query impact --node "Orders" --direction upstream --graph graph.json

# Scope a PR's impact to changed files only
synopsis git-scan /path/to/workspace --base main --json

# Compare two graph snapshots
synopsis diff before.json after.json --json

# Classify breaking changes
synopsis breaking-diff before.json after.json --json

# Print version
synopsis --version
```

All commands support `--json` for machine-readable output: `{"command":"...","ok":true,"result":{...},"ms":142}`.

### Full command reference

```
synopsis scan <rootPath> [-o graph.json] [--exclude <path>...] [--json]
synopsis watch <rootPath> [-o graph.json] [--debounce-ms 1500]
synopsis export json|csv|jsonl <rootPath> -o <file|folder>
synopsis query impact --node <id> [--direction upstream|downstream] [--graph graph.json] [--json]
synopsis query paths --from <node> --to <node> [--graph graph.json] [--json]
synopsis query symbol --fqn <name> [--blast-radius] [--depth 4] [--graph graph.json] [--json]
synopsis query ambiguous [--graph graph.json] [--limit 50] [--json]
synopsis git-scan <rootPath> --base <branch> [--head HEAD] [--depth 4] [--json]
synopsis diff <before.json> <after.json> [--json]
synopsis breaking-diff <before.json> <after.json> [--json] [-o report.json]
synopsis mcp (--root <rootPath> | --graph <graph.json>) [--socket <path> | --tcp <addr>] [--state-dir <path>]
```

### Daemon mode (multi-repo, persistent)

For large workspaces with many repos, run Synopsis as a persistent daemon. It holds a live combined graph in memory and serves queries without re-scanning:

```bash
# Unix socket (recommended for local AI agent use)
synopsis mcp --root /path/to/workspace --socket /tmp/synopsis.sock --state-dir ~/.synopsis/ws

# TCP
synopsis mcp --root /path/to/workspace --tcp localhost:5100 --state-dir ~/.synopsis/ws
```

State is persisted to `--state-dir` and restored on restart. Use `reindex_repository` to refresh individual repos incrementally.

### Cross-service HTTP resolution tips

Synopsis traces HTTP calls across microservice boundaries. Resolution quality depends on how your services are configured.

**For best results:**
1. **Use named HTTP clients** — `services.AddHttpClient("CatalogApi", ...)` is the strongest signal.
2. **Keep `appsettings.json` in your repos** — Synopsis reads base URLs from them. Even placeholder values help:
   ```json
   { "Services": { "CatalogApi": { "BaseUrl": "http://catalog-api" } } }
   ```
3. **Name repos/projects consistently with client names** — `CatalogClient` auto-matches repo `catalog-api` / project `Catalog.Api`.

Unresolvable calls (`new HttpClient()`, dynamic URLs, runtime-built paths) show up as `Certainty: Ambiguous` — which is the correct answer. Audit them with `synopsis query ambiguous`.

Full details: [skills/dotnet-techne-synopsis/http-resolution.md](skills/dotnet-techne-synopsis/http-resolution.md)

---

## Manual installation

### Clone the repository

```bash
git clone https://github.com/Metalnib/dotnet-episteme-skills.git ~/.local/share/dotnet-episteme-skills
```

### Claude Code

Skills load from `~/.claude/skills` (global) or `.claude/skills` (project).

```bash
# All skills
cp -R ~/.local/share/dotnet-episteme-skills/skills/* ~/.claude/skills/

# Synopsis only
cp -R ~/.local/share/dotnet-episteme-skills/skills/dotnet-techne-synopsis ~/.claude/skills/
```

### OpenCode

```bash
cp -R ~/.local/share/dotnet-episteme-skills/skills/* ~/.config/opencode/skill/
```

### Other Agent Skills-compatible tools

Copy any skill folder that contains `SKILL.md` (keep companion scripts and binaries in the same folder) and point your tool to that directory.

### Keeping skills up to date

```bash
git -C ~/.local/share/dotnet-episteme-skills pull
# Then re-copy updated folders to your tool's skill directory
```

---

## Advanced / Contributing

### Repository layout

```
skills/                  One folder per skill, each with SKILL.md
src/synopsis/            Synopsis source code (.NET 10)
  Synopsis.Analysis/     Roslyn analysis, graph model, querying
  Synopsis/              CLI, MCP server, JSON output
  Synopsis.Tests/        155 unit tests
scripts/                 Validation and CI helpers
.claude-plugin/          Plugin manifest and marketplace metadata
.github/workflows/       Tag-driven release pipeline
```

### Building Synopsis from source

Requires **.NET 10 SDK**.

```bash
cd src/synopsis
dotnet build Synopsis.sln -c Release
dotnet test Synopsis.Tests -c Release --no-build

# Publish all platforms into skills/dotnet-techne-synopsis/bin/
./publish-all.sh

# Or a single platform
dotnet publish Synopsis/Synopsis.csproj -c Release -r osx-arm64
dotnet publish Synopsis/Synopsis.csproj -c Release -r linux-x64
dotnet publish Synopsis/Synopsis.csproj -c Release -r win-x64
```

### Architecture notes

- **.NET 10**, Roslyn 5.0 semantic analysis
- **Source-generated STJ** — zero reflection in serialization (`SynopsisJsonContext`, `McpJsonContext`)
- **FrozenDictionary / ImmutableArray** throughout for allocation-free hot paths
- **R2R single-file, self-contained** — ships the .NET runtime; no SDK on target machines
- Version is defined once in `src/synopsis/Directory.Build.props` and flows into the binary, MCP server info, and `synopsis --version`

### Releasing

1. Update `CHANGELOG.md` with what's new.
2. Bump `<Version>` in `src/synopsis/Directory.Build.props`.
3. Update `.claude-plugin/plugin.json` and `.claude-plugin/marketplace.json` to the same version.
4. Run local validation:
   ```bash
   bash scripts/validate.sh
   bash scripts/validate-marketplace.sh
   ```
5. Tag and push — CI builds all 6 platform binaries, runs tests, and creates the GitHub release automatically:
   ```bash
   git tag v1.5.0
   git push origin main v1.5.0
   ```

### Validate repository

```bash
bash scripts/validate.sh
bash scripts/validate-marketplace.sh
```

---

## Specification references

- Agent Skills spec: https://agentskills.io/specification
- Claude Code skills docs: https://code.claude.com/docs/en/skills
- OpenCode skills docs: https://opencode.ai/docs/skills

## License

MIT
