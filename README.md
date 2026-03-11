# DotNet Episteme Skills
Production-tested .NET skills repository for AI coding tools.
These skills provide practical, real-world, enterprise-proven instructions that help AI agents implement, review, and improve .NET systems with production readiness in mind.

## What kind of skills are provided?

### Static analysis and architecture
- `dotnet-techne-synopsis` (`1.0.0`) — **static dependency and blast-radius explorer for .NET workspaces**. Scans multi-repo .NET codebases via Roslyn, builds a full dependency graph (controllers, endpoints, methods, call chains, DI resolution, HTTP clients, EF Core lineage, cross-repo boundaries), and answers blast-radius queries. Ships as a self-contained binary for macOS, Windows, and Linux — no SDK required. Includes MCP server mode for persistent AI agent integration. [See full details below.](#synopsis)

### Code review and risk analysis
- `dotnet-techne-code-review` (`1.2.0`) — end-to-end .NET review: correctness, performance, security, data, messaging, observability.
- `dotnet-techne-crap-analysis` (`1.0.0`) — CRAP score and coverage hotspot analysis.

### API and coding design
- `dotnet-techne-csharp-api-design` (`1.0.0`) — compatibility-safe API evolution and versioning.
- `dotnet-techne-csharp-coding-standards` (`1.0.0`) — modern C# coding standards and refactoring guidance.

### Performance and concurrency
- `dotnet-techne-csharp-concurrency-patterns` (`1.0.0`) — choosing async/channels/dataflow/Rx patterns.
- `dotnet-techne-csharp-type-design-performance` (`1.0.0`) — readonly structs, sealed types, spans, frozen collections.
- `dotnet-techne-inspect` (`1.2.0`) — inspect NuGet package APIs and decompile signatures.

### Serialisation and contracts
- `dotnet-techne-serialisation` (`1.0.0`) — serialisation format and wire compatibility decisions.

---

## Synopsis

Synopsis is a static dependency and blast-radius explorer for .NET workspaces. It uses Roslyn's semantic analysis to build a full dependency graph and answer architectural queries that AI agents need when operating on large codebases.

### What it discovers

| Layer | What Synopsis extracts |
|---|---|
| **Workspace topology** | Repositories, solutions, projects, project references, cross-repo boundaries |
| **Type structure** | Classes, interfaces, implementations, constructor injection (DI) |
| **Call graph** | Method-to-method calls, interface dispatch resolution via DI registrations |
| **ASP.NET endpoints** | Controller actions, minimal API `MapGet`/`MapPost`, route templates |
| **HTTP clients** | `HttpClient`/`IHttpClientFactory` calls, base URLs from config, cross-service resolution |
| **EF Core lineage** | `DbContext` -> `DbSet<T>` -> entity -> table, `ToTable()` mappings, raw SQL table references |
| **Certainty tracking** | Every node and edge has a certainty level: Exact, Inferred, Ambiguous, or Unresolved |

### Pre-built binaries (auto-downloaded on first use)

No SDK or manual installation needed. The detect script auto-downloads the correct self-contained binary from GitHub Releases on first use:

```bash
# macOS/Linux — downloads if missing, then prints binary path
./skills/dotnet-techne-synopsis/scripts/detect-tool.sh

# Windows
.\skills\dotnet-techne-synopsis\scripts\detect-tool.ps1
```

| Platform | Size | Dependencies for download |
|---|---|---|
| macOS Apple Silicon | ~207 MB | `curl` (pre-installed) |
| Windows x64 | ~179 MB | PowerShell 5+ (built-in) |
| Linux x64 | ~182 MB | `curl` or `wget` |

The binary is cached in `skills/dotnet-techne-synopsis/bin/{platform}/` after first download. Subsequent runs are instant.

### Quick start

```bash
# Scan a workspace
synopsis scan /path/to/workspace -o graph.json

# What's the blast radius of OrdersController?
synopsis query symbol --fqn "OrdersController" --blast-radius --graph graph.json

# What calls into the Orders table?
synopsis query impact --node "Orders" --direction upstream --graph graph.json

# Find paths between two nodes
synopsis query paths --from "OrdersController" --to "OrderRepository" --graph graph.json

# Audit unresolved/ambiguous edges
synopsis query ambiguous --graph graph.json

# Machine-readable JSON output (all commands)
synopsis query impact --node "OrdersController" --json --graph graph.json
```

### All commands

```
synopsis scan <rootPath> [-o graph.json] [--exclude <path> ...] [--json]
synopsis watch <rootPath> [-o graph.json] [--debounce-ms 1500]
synopsis export json|csv|jsonl <rootPath> -o <file|folder>
synopsis query impact --node <id> [--direction upstream|downstream] [--graph graph.json] [--json]
synopsis query paths --from <node> --to <node> [--graph graph.json] [--json]
synopsis query symbol --fqn <name> [--blast-radius] [--depth 4] [--graph graph.json] [--json]
synopsis query ambiguous [--graph graph.json] [--limit 50] [--json]
synopsis git-scan <rootPath> --base <branch> [--head HEAD] [--depth 4] [--json]
synopsis diff <before.json> <after.json> [--json]
synopsis mcp --root <rootPath> | --graph <graph.json>
```

### MCP server mode

Synopsis can run as a persistent MCP (Model Context Protocol) server over stdin/stdout. AI agents connect to it and call tools without re-scanning:

```bash
synopsis mcp --root /path/to/workspace
# Or from a pre-scanned graph:
synopsis mcp --graph graph.json
```

Available MCP tools: `blast_radius`, `find_paths`, `list_endpoints`, `list_nodes`, `node_detail`, `db_lineage`, `cross_repo_edges`, `ambiguous_review`, `scan_stats`.

### Git-aware analysis

Scope analysis to files changed in a PR:
```bash
synopsis git-scan /path/to/workspace --base main --json
```
This runs `git diff`, maps changed files to graph nodes, and expands the blast radius from each affected node.

### Comparing two scans

```bash
synopsis diff before.json after.json --json
```
Compares two graph snapshots by stable node/edge IDs. Reports added, removed, and changed nodes and edges with statistics deltas.

### Output contract

When `--json` is used, stdout contains a single JSON envelope:
```json
{"command":"query impact","ok":true,"result":{...},"ms":142}
```
All diagnostics and progress go to stderr. stdout is always machine-readable when `--json` is specified.

### Cross-service HTTP resolution

Synopsis traces HTTP calls across microservice boundaries by matching outbound `HttpClient` calls to internal endpoints in other repositories. Resolution quality depends on how your services are configured.

**What Synopsis reads:**
- `IHttpClientFactory.CreateClient("name")` calls — the client name is the primary signal
- Typed HTTP client class names (e.g., `CatalogClient`, `PaymentGateway`)
- `appsettings*.json` files — searches for base URL configuration keys
- String literal request paths in `GetAsync`, `PostAsJsonAsync`, etc.

**How it resolves targets:**
Synopsis normalizes the client name / base URL host into a stem (`CatalogClient` → `catalog`, `payment-api` → `payment`) and matches it against repo and project names. This means `CatalogClient` calling `/products/{id}` correctly resolves to `GET /products/{id}` in repo `catalog-api` (project `Catalog.Api`) rather than matching a `/products` endpoint in an unrelated service.

**For best results across 30+ repos:**

1. **Use named HTTP clients** — `services.AddHttpClient("CatalogApi", ...)` gives Synopsis the strongest signal
2. **Keep `appsettings.json` files in your repos** — Synopsis reads them for base URLs. If your CI/CD strips them, keep a template or development version committed:
   ```json
   {
     "Services": {
       "CatalogApi": { "BaseUrl": "http://catalog-api" },
       "PaymentService": { "BaseUrl": "http://payment-service" }
     }
   }
   ```
   Even with placeholder hostnames, Synopsis uses the key structure (`Services:CatalogApi:BaseUrl`) and host value (`catalog-api`) to correlate clients to target repos.

3. **Name repos/projects consistently with clients** — if the client is `CatalogClient`, the target repo should contain `catalog` somewhere (`catalog-api`, `repo-catalog`, `Catalog.Api`). Synopsis strips common suffixes (`Client`, `Service`, `Api`, `Gateway`) and prefixes (`repo-`, `svc-`) before matching.

4. **Mark repo boundaries** — place a `.synopsis-repo` empty marker file (or have `.git`) at each repo root so Synopsis detects repository boundaries and can flag cross-repo edges.

**What stays ambiguous:**
- `new HttpClient()` with no named client and no config — no service identity to match
- Dynamic URLs built at runtime from variables — Synopsis only reads compile-time string literals
- `SendAsync(new HttpRequestMessage(...))` — request path isn't extractable

These produce `Certainty: Ambiguous` or `Certainty: Unresolved` edges — which is the correct answer. Run `synopsis query ambiguous` to audit them.

Full details: [skills/dotnet-techne-synopsis/http-resolution.md](skills/dotnet-techne-synopsis/http-resolution.md)

### Building from source (optional)

Only needed if you want to modify Synopsis or rebuild binaries. Requires **.NET 10 SDK**.

```bash
cd src/synopsis
dotnet build Synopsis.sln           # Build
dotnet test Synopsis.Tests           # Run 35 unit tests
```

Publish all platforms into the skill folder:
```bash
./src/synopsis/publish-all.sh
```

Or publish a single platform:
```bash
cd src/synopsis
dotnet publish Synopsis/Synopsis.csproj -c Release -r osx-arm64    # macOS
dotnet publish Synopsis/Synopsis.csproj -c Release -r win-x64      # Windows
dotnet publish Synopsis/Synopsis.csproj -c Release -r linux-x64    # Linux
```
The `PublishDir` is configured in the csproj to output directly to `skills/dotnet-techne-synopsis/bin/{RID}/`.

### Architecture

Synopsis is built as 2 projects:

```
src/synopsis/
  Synopsis.Analysis/    .NET 10 class library — all Roslyn analysis, graph building, querying
  Synopsis/             .NET 10 console app — CLI commands, MCP server, JSON output
  Synopsis.Tests/       35 unit tests (GraphBuilder, GraphQuery, Paths, GraphDiffer, JSON round-trip)
```

Key technical decisions:
- **.NET 10** with Roslyn 5.3.0 semantic analysis
- **Source-generated System.Text.Json** — zero reflection in serialization
- **FrozenDictionary/ImmutableArray** throughout for cache-friendly, zero-allocation hot paths
- **Parallel analysis passes** with thread-safe graph builder
- **R2R single-file, self-contained** — ships .NET runtime, no SDK on target
- Only .NET Core/.NET 5+ projects supported (net472 stripped)

### Upgrading Synopsis

1. Pull latest source
2. Rebuild and republish:
```bash
cd src/synopsis
dotnet build Synopsis.sln -c Release
dotnet test Synopsis.Tests -c Release --no-build
./publish-all.sh
```
3. The binaries in `skills/dotnet-techne-synopsis/bin/` are updated in place

To upgrade just the NuGet packages:
```bash
cd src/synopsis
# Edit Directory.Packages.props with new versions
dotnet restore Synopsis.sln
dotnet build Synopsis.sln -c Release
dotnet test Synopsis.Tests -c Release --no-build
./publish-all.sh
```

---

## Repository layout
- Each skill is in `skills/<skill-id>/`.
- Every skill has a `SKILL.md` entrypoint.
- Some skills include companion docs, scripts, and pre-built binaries; keep the whole folder together when installing manually.
- Synopsis source code lives in `src/synopsis/`.

## Install via Claude marketplace (recommended for Claude Code)
Add this repository as a marketplace:
```text
/plugin marketplace add Metalnib/dotnet-episteme-skills
```

Then install the plugin:
```text
/plugin
```
Use **Browse Plugins** and install `dotnet-episteme-skills`.

Direct command install (if you prefer CLI commands):
1. Run `/plugin marketplace list`
2. Note the marketplace name shown by Claude
3. Install:
```text
/plugin install dotnet-episteme-skills@<marketplace-name>
```

After installation, restart Claude Code so new skills are loaded.

## Manual file-copy installation (AI tool of your choice)
### 1) Clone this repository
```bash
git clone https://github.com/Metalnib/dotnet-episteme-skills.git ~/.local/share/dotnet-episteme-skills
```

### 2) Install into your tool's skill directory
You can copy all skills or only selected skill folders.

### Claude Code
Claude Code loads skills from:
- Global: `~/.claude/skills`
- Project: `.claude/skills`

Install all skills globally:
```bash
mkdir -p ~/.claude/skills
cp -R ~/.local/share/dotnet-episteme-skills/skills/* ~/.claude/skills/
```

Install one skill only:
```bash
mkdir -p ~/.claude/skills
cp -R ~/.local/share/dotnet-episteme-skills/skills/dotnet-techne-synopsis ~/.claude/skills/
```

### OpenCode
OpenCode loads skills from:
- Global: `~/.config/opencode/skill`
- Project: `.opencode/skill`

Install all skills globally:
```bash
mkdir -p ~/.config/opencode/skill
cp -R ~/.local/share/dotnet-episteme-skills/skills/* ~/.config/opencode/skill/
```

### Other Agent Skills-compatible tools
If your tool supports Agent Skills format:
- copy any skill folder that contains `SKILL.md`
- keep companion files (scripts, binaries) in the same folder
- point your tool to that skills directory according to its documentation

## Invocation behavior in this repo
Each skill is configured to be invocable by both model and user:
- `disable-model-invocation: false`
- `user-invocable: true`

`metadata.trigger_keywords` is included as extra routing context. Tools that do not support it can safely ignore it.

## CI publishing and releases
This repository uses tag-driven publishing with validation gates:
- validates plugin and skill registry
- validates marketplace metadata alignment
- creates a GitHub release with a packaged archive

### Release workflow trigger
- workflow file: `.github/workflows/release.yml`
- trigger: push tag `v*` (example: `v1.3.0`)

### Before releasing
1. Update version in `.claude-plugin/plugin.json`
2. Set matching version in `.claude-plugin/marketplace.json` (`plugins[0].version`)
3. Rebuild Synopsis binaries: `./src/synopsis/publish-all.sh`
4. Run local validation:
```bash
bash scripts/validate.sh
bash scripts/validate-marketplace.sh
```

### Publish a release
```bash
git tag v1.3.0
git push origin v1.3.0
```

## Update
```bash
git -C ~/.local/share/dotnet-episteme-skills pull
```

If you used copy-based install, copy updated skill folders again to your tool directory.

## Validate repository
```bash
bash scripts/validate.sh
bash scripts/validate-marketplace.sh
```

## Specification and docs references
- Agent Skills specification: https://agentskills.io/specification
- Claude Code skills docs: https://code.claude.com/docs/en/skills
- OpenCode skills docs: https://opencode.ai/docs/skills

## License
MIT
