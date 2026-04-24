# DotNet Episteme Skills

## Why these skills exist

AI coding tools can generate hundreds of source files in minutes. That speed is the point - but it shifts the bottleneck from writing code to trusting it.

The code compiles, the tests pass, and it still ships. Async operations quietly ignore cancellation signals. Buffers grow without bound until the process crashes under load. Sensitive user data flows into log files on every request. Public API contracts change without a deprecation path, silently breaking every caller that hasn't recompiled. Interfaces get refactored in one repository while nobody knows which of the other fourteen depend on them.

These aren't edge cases - they're the specific failure modes that general-purpose AI doesn't know to look for, and that tired humans miss in review. These skills exist to catch them before they reach production.

In a system with dozens or hundreds of microservices, where AI is generating implementations faster than any team can meaningfully review, the question isn't whether bad code will be written - it's whether you'll catch it before it ships. Most teams don't have the bandwidth or the .NET depth to review every generated service thoroughly. These skills fill that gap.

---

## Install

Claude Code - one command, skills activate automatically:

```text
/plugin marketplace add Metalnib/dotnet-episteme-skills
```

Other tools (OpenCode, etc.) - download the latest archive from the [releases page](https://github.com/Metalnib/dotnet-episteme-skills/releases), extract, and copy `skills/` to your tool's skills directory:

```bash
# Linux / macOS - extract then copy
tar -xzf dotnet-episteme-skills-1.5.0.tar.gz
cp -R skills/* ~/.claude/skills/          # Claude Code
cp -R skills/* ~/.config/opencode/skill/  # OpenCode
```

```powershell
# Windows
Expand-Archive dotnet-episteme-skills-1.5.0.zip .
Copy-Item -Recurse skills\* "$env:USERPROFILE\.claude\skills\"
```

Or clone for easy updates:

```bash
git clone https://github.com/Metalnib/dotnet-episteme-skills.git
cp -R dotnet-episteme-skills/skills/* ~/.claude/skills/
# update later: git -C dotnet-episteme-skills pull && cp -R dotnet-episteme-skills/skills/* ~/.claude/skills/
```

---

## Skills

| Skill | What it does | When to use it |
|---|---|---|
| `dotnet-techne-code-review` | Production-readiness review of .NET code: correctness, security, performance, data access, messaging, observability | After every AI-generated implementation, before any PR |
| `dotnet-techne-synopsis` | Roslyn-based dependency graph across all your repos: blast radius, cross-service call map, EF Core lineage, breaking-change diff | "What breaks if I change this?" across a microservices landscape |
| `dotnet-techne-inspect` | Decompiles and surfaces the public API of any NuGet package - internal or third-party | Before writing code against a package where you don't have the source |
| `dotnet-techne-crap-analysis` | Finds high-complexity, low-coverage methods (CRAP score) | Deciding where to focus test effort in a large codebase |
| `dotnet-techne-csharp-api-design` | Breaking-change detection, versioning strategy, deprecation paths, compatibility shims | Evolving a public or shared API without breaking callers |
| `dotnet-techne-csharp-coding-standards` | Modern C# idiom review: records, patterns, nullability, immutability | "Is this idiomatic?" or general refactoring guidance |
| `dotnet-techne-csharp-concurrency-patterns` | Async primitive selection, backpressure, lock-free patterns, deadlock detection | Choosing between async/await, `Channel<T>`, Dataflow, or Rx |
| `dotnet-techne-csharp-type-design-performance` | Hot-path allocations, `readonly struct`, `FrozenDictionary`, `Span<T>`, sealed types | Performance-sensitive or high-throughput code paths |
| `dotnet-techne-serialisation` | Wire contract safety, STJ source-gen, AOT compatibility, backwards compatibility across versions | Cross-service serialisation and versioned message schemas |

---

## Code review - the most important skill

The `dotnet-techne-code-review` skill performs end-to-end production-readiness review of .NET code changes. It's designed for the AI-coding workflow: you (or an agent) generate an implementation, then immediately review it before it ever reaches a human or a PR.

The review covers correctness (exception safety, CancellationToken propagation, thread-safety, retry and idempotency), performance (allocations on hot paths, unbounded buffers, missing backpressure, AOT/trimming), security (input validation, auth boundaries, SSRF controls, secrets in logs), data access (N+1 queries, missing indexes, EF Core misuse), messaging (ordering, poison messages, at-least-once safety), and observability (structured logging, metrics, correlation IDs).

Two modes are available. Standard is the default - balanced coverage of correctness, maintainability, and risk. Cynical/Adversarial mode assumes defects exist until disproven, generates at least five failure hypotheses, and validates each with evidence before reporting. Use it when the code is on a critical path or when you want the hardest possible challenge.

```
Review the new OrdersController I just generated.

Critical review of this branch.

Cynical review of this PR before I merge.

Security-focused review of PR #247 (auth middleware).

Tear apart this message consumer - it'll be processing 250k messages/hour.
```

---

## Synopsis - dependency graph and blast-radius analysis

The `dotnet-techne-synopsis` skill answers the architectural questions AI agents can't answer from code reading alone: what breaks if I change this, which services call that endpoint, what HTTP traffic touches this database table. It uses Roslyn to scan your entire .NET workspace and builds a live semantic dependency graph.

Every node and edge carries a certainty level (Exact, Inferred, Ambiguous, or Unresolved) so you always know how much to trust a result. The graph covers repositories, projects, types, DI registrations, method-to-method call chains, ASP.NET endpoints, HTTP client calls, EF Core entity and table mappings, and NuGet packages with per-project version tracking.

```
Blast radius of this IPaymentService contract change.

If I change IOrderRepository's signature, what breaks across our repos?

Show me all HTTP endpoints that eventually write to the Orders table.

Which services call POST /api/payments? I need to change its contract.

Which teams depend on PrimeLabs.Contracts 2.x and what version are they on?
```

CLI quick start:

```bash
synopsis scan /path/to/workspace -o graph.json
synopsis query symbol --fqn "OrdersController" --blast-radius --graph graph.json
synopsis query impact --node "Orders" --direction upstream --graph graph.json
synopsis git-scan /path/to/workspace --base main --json
synopsis breaking-diff before.json after.json --json
synopsis --version
```

All commands support `--json`: `{"command":"...","ok":true,"result":{...},"ms":142}`.

Full command reference:

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

MCP tools (available to AI agents in daemon mode):

| Tool | What it answers |
|---|---|
| `blast_radius` | What does changing this symbol break? |
| `find_paths` | How does A connect to B? |
| `endpoint_callers` | Every service that calls a given HTTP endpoint, with certainty |
| `table_entry_points` | Which HTTP endpoints eventually read or write a database table? |
| `repo_dependency_matrix` | Service-to-service HTTP call map across all repos |
| `package_dependents` | Which repos depend on a NuGet package, and at what version? |
| `db_lineage` | EF Core chain from DbContext down to the database table |
| `breaking_diff` | Classify breaking changes between two graph snapshots |
| `list_endpoints` | HTTP endpoints filtered by project or verb |
| `node_detail` | Everything known about one node - all edges in and out |
| `cross_repo_edges` | All calls that cross repository boundaries |
| `ambiguous_review` | Unresolved and ambiguous edge audit |
| `scan_stats` | Scan metadata and statistics |
| `list_repositories` | Repos tracked by the daemon with scan timestamps |
| `reindex_repository` | Re-scan one repo and update the live graph |
| `reindex_all` | Re-scan every tracked repo |

Daemon mode (multi-repo, persistent - for large workspaces):

```bash
synopsis mcp --root /path/to/workspace --socket /tmp/synopsis.sock --state-dir ~/.synopsis/ws
```

The daemon holds a live graph in memory, persists state across restarts, and serves queries without re-scanning. Use `reindex_repository` to refresh individual repos incrementally.

Cross-service HTTP resolution tips:
1. Use named HTTP clients - `services.AddHttpClient("CatalogApi", ...)` is the strongest signal.
2. Keep `appsettings.json` in your repos - even placeholder base URLs help:
   ```json
   { "Services": { "CatalogApi": { "BaseUrl": "http://catalog-api" } } }
   ```
3. Name repos/projects consistently with client names - `CatalogClient` auto-matches `catalog-api` / `Catalog.Api`.

Unresolvable calls show up as `Certainty: Ambiguous` - run `synopsis query ambiguous` to audit them. Full details: [skills/dotnet-techne-synopsis/http-resolution.md](skills/dotnet-techne-synopsis/http-resolution.md)

---

## Inspect - internal and proprietary NuGet package explorer

The `dotnet-techne-inspect` skill decompiles and surfaces the public API of any NuGet package - primarily useful for internal and proprietary packages where you don't have the source. The typical workflow is: inspect the package, understand its API, then generate code against it in the same step. It also covers third-party packages where the source isn't accessible, and version-to-version API comparisons.

Requires `dotnet-inspect` or `ilspycmd` global tool installed.

```
Inspect our internal Payments.Contracts 3.1 package and implement the new webhook handler.

Decompile the legacy OrderCore library - I need to know what's available before generating the adapter.

Inspect our shared Infrastructure.Auth package and wire up authentication in this new service.

What changed in PrimeLabs.Messaging between 4.1 and 4.2? I need to migrate this consumer.
```

---

## Other skills

These skills handle specific .NET engineering concerns. Each activates automatically when you describe the relevant problem.

| Skill | Best for |
|---|---|
| `dotnet-techne-crap-analysis` | "Which methods are highest risk (high complexity, low coverage)?" Prioritises where to focus test effort. |
| `dotnet-techne-csharp-api-design` | Designing a new public API or evolving an existing one without breaking callers. Covers versioning, deprecation, compatibility shims. |
| `dotnet-techne-csharp-coding-standards` | "Is this idiomatic modern C#?" Refactoring guidance, pattern choices, maintainability. |
| `dotnet-techne-csharp-concurrency-patterns` | Choosing between async/await, `Channel<T>`, `IAsyncEnumerable`, Dataflow, Rx. Avoiding lock contention and deadlocks. |
| `dotnet-techne-csharp-type-design-performance` | Hot-path type decisions: `readonly struct` vs class, `FrozenDictionary`, `Span<T>`, sealed types, allocation profiling. |
| `dotnet-techne-serialisation` | Picking a serialisation format and keeping wire contracts backwards-compatible across services and versions. |

---

## Advanced / Contributing

### Repository layout

```
skills/                  One folder per skill, each with SKILL.md
src/synopsis/            Synopsis source (.NET 10)
  Synopsis.Analysis/     Roslyn analysis, graph model, querying
  Synopsis/              CLI, MCP server, JSON output
  Synopsis.Tests/        155 unit tests
scripts/                 Validation and CI helpers
.claude-plugin/          Plugin manifest and marketplace metadata
.github/workflows/       Tag-driven release pipeline
CHANGELOG.md             Version history
```

### Building Synopsis from source

Requires .NET 10 SDK.

```bash
cd src/synopsis
dotnet build Synopsis.sln -c Release
dotnet test Synopsis.Tests -c Release --no-build
./publish-all.sh          # all 6 platforms -> skills/dotnet-techne-synopsis/bin/
```

Version is defined once in `src/synopsis/Directory.Build.props` and flows into the binary, `synopsis --version`, and the MCP `initialize` response.

### Releasing

1. Update `CHANGELOG.md`.
2. Bump `<Version>` in `src/synopsis/Directory.Build.props`.
3. Sync `.claude-plugin/plugin.json` and `.claude-plugin/marketplace.json` to the same version.
4. Validate locally: `bash scripts/validate.sh && bash scripts/validate-marketplace.sh`
5. Tag and push - CI builds all 6 binaries, runs 155 tests, and publishes the GitHub release:
   ```bash
   git tag v1.5.0
   git push origin main v1.5.0
   ```

---

## Specification references

- Agent Skills spec: https://agentskills.io/specification
- Claude Code skills: https://code.claude.com/docs/en/skills
- OpenCode skills: https://opencode.ai/docs/skills

## License

MIT
