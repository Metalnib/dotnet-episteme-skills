---
name: dotnet-techne-synopsis
description: Use when you need blast-radius analysis, dependency graphs, cross-repo impact, or architectural overview of .NET workspaces. Keywords: blast radius, dependency graph, impact analysis, cross-repo, call graph, endpoint map, EF Core lineage.
disable-model-invocation: false
user-invocable: true
license: MIT
compatibility: Pre-built binaries included for osx-arm64, win-x64, linux-x64. No SDK required.
metadata:
  author: Metalnib
  version: "1.0.0"
  trigger_keywords:
    - blast radius
    - dependency graph
    - impact analysis
    - cross-repo dependencies
    - call graph
    - endpoint map
    - ef core lineage
    - what does this change affect
    - architectural overview
---

# Synopsis - .NET Workspace Dependency & Blast-Radius Explorer

Static analysis tool that scans .NET workspaces via Roslyn, builds a dependency graph, and answers blast-radius queries. Designed for AI agents operating on large multi-repo .NET codebases.

## Requirements

No SDK or manual installation needed. The detect script auto-downloads the correct binary on first use.

| Platform | Binary | Downloaded from |
|---|---|---|
| macOS Apple Silicon | `bin/osx-arm64/synopsis` | GitHub Release `synopsis-osx-arm64.tar.gz` |
| Windows x64 | `bin/win-x64/synopsis.exe` | GitHub Release `synopsis-win-x64.zip` |
| Linux x64 | `bin/linux-x64/synopsis` | GitHub Release `synopsis-linux-x64.tar.gz` |

External dependencies for auto-download:
- **macOS/Linux:** `curl` (pre-installed) or `wget`
- **Windows:** PowerShell 5+ (built-in `System.Net.Http`)

## Install (optional - build from source)

Only needed if you want to rebuild or no GitHub Release is available. Requires .NET 10+ SDK.

**Bash (Linux/macOS):**
```bash
./skills/dotnet-techne-synopsis/scripts/install.sh
```

**PowerShell (Windows):**
```powershell
.\skills\dotnet-techne-synopsis\scripts\install.ps1
```

## When to use this skill

Use when the user asks to:
- Analyze blast radius of a code change
- Map dependencies across .NET repos
- Find what endpoints/services are affected by a change
- Trace call chains from controller to database
- Review cross-repo boundaries
- Audit ambiguous/unresolved dependencies
- Compare architecture between two snapshots (diff)
- Scope a PR's impact via git diff

## Workflow

### Step 0: Detect tool

**Bash:**
```bash
./skills/dotnet-techne-synopsis/scripts/detect-tool.sh
```

**PowerShell:**
```powershell
.\skills\dotnet-techne-synopsis\scripts\detect-tool.ps1
```

### Step 1: Scan a workspace

```bash
synopsis scan /path/to/workspace -o graph.json
```

This produces `graph.json` containing all nodes (repos, projects, controllers, methods, endpoints, HTTP clients, DB contexts, entities, tables) and edges (calls, injects, implements, cross-repo, etc.).

### Step 2: Query the graph

**Blast radius of a symbol:**
```bash
synopsis query symbol --fqn "OrdersController" --blast-radius --graph graph.json
synopsis query symbol --fqn "IOrderRepository" --blast-radius --direction upstream --graph graph.json
```

**Impact analysis:**
```bash
synopsis query impact --node "OrdersController" --direction downstream --graph graph.json --json
synopsis query impact --node "Orders" --direction upstream --graph graph.json --json
```

**Find paths between nodes:**
```bash
synopsis query paths --from "OrdersController" --to "Orders" --graph graph.json --json
```

**Audit ambiguous edges:**
```bash
synopsis query ambiguous --graph graph.json --json
```

### Step 3: Git-aware analysis

Scope analysis to files changed in a PR:
```bash
synopsis git-scan /path/to/workspace --base main --json
```

### Step 4: Diff two scans

Compare architecture before/after a change:
```bash
synopsis diff before.json after.json --json
```

### Step 5: MCP server mode

For persistent AI agent integration (stdin/stdout JSON-RPC):
```bash
synopsis mcp --root /path/to/workspace
```

Or from a pre-scanned graph:
```bash
synopsis mcp --graph graph.json
```

## Quick Reference

```bash
# === Full workflow ===
synopsis scan /workspace -o graph.json                              # 1. Scan
synopsis query symbol --fqn "OrdersController" --blast-radius       # 2. Blast radius
synopsis query impact --node "Orders" --direction upstream --json   # 3. Upstream impact
synopsis git-scan /workspace --base main --json                     # 4. PR impact

# === Export formats ===
synopsis export json /workspace -o graph.json
synopsis export csv /workspace -o output/
synopsis export jsonl /workspace -o graph.jsonl

# === All commands support --json for machine-readable output ===
synopsis scan /workspace --json          # JSON envelope to stdout
synopsis query impact --node X --json    # JSON envelope to stdout
```

## Output Contract

When `--json` is used, stdout contains a single JSON envelope:
```json
{"command":"query impact","ok":true,"result":{...},"ms":142}
```

All diagnostics and progress go to stderr. stdout is always machine-readable when `--json` is specified.

## MCP Tools Available

When running in MCP mode (`synopsis mcp`), the following tools are exposed:

| Tool | Description |
|---|---|
| `blast_radius` | Upstream/downstream impact subgraph for a symbol |
| `find_paths` | All paths between two nodes |
| `list_endpoints` | HTTP endpoints filtered by project/verb |
| `list_nodes` | Nodes filtered by type/project/query |
| `node_detail` | Single node with all incoming/outgoing edges |
| `db_lineage` | EF Core entity-to-table lineage chain |
| `cross_repo_edges` | All cross-repository boundary edges |
| `ambiguous_review` | Unresolved and ambiguous edge audit |
| `scan_stats` | Scan statistics and metadata |

## HTTP Cross-Service Resolution

Synopsis traces HTTP calls across microservice boundaries. For best resolution accuracy:

- **Use `IHttpClientFactory` with named clients** — the client name is the strongest signal for matching
- **Put base URLs in appsettings with descriptive keys** — `Services:CatalogApi:BaseUrl` not generic `ApiUrl`
- **Name repos and projects consistently** — `CatalogClient` auto-matches repo `catalog-api` and project `Catalog.Api`

See [http-resolution.md](http-resolution.md) for the full guide on supported patterns, appsettings configuration, service-affinity matching, certainty levels, and recommendations.

## Notes

- Synopsis uses Roslyn MSBuildWorkspace for semantic analysis - it understands DI registration, interface dispatch, call graphs, and EF Core mappings
- The tool targets .NET Core/.NET 5+ projects only (no .NET Framework 4.x support)
- First scan of a large workspace takes 15-60s; subsequent queries on loaded graph.json are instant
- MCP mode scans once at startup, then serves queries with no re-scan overhead
- Graph node IDs are SHA256-based and stable across scans of the same codebase
