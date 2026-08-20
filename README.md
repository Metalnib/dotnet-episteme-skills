# DotNet Episteme Skills

## Why these skills exist

AI coding tools can generate hundreds of source files in minutes. That speed is the point - but it shifts the bottleneck from writing code to trusting it.

The code compiles, the tests pass, and it still ships. Async operations quietly ignore cancellation signals. Buffers grow without bound until the process crashes under load. Sensitive user data flows into log files on every request. Public API contracts change without a deprecation path, silently breaking every caller that hasn't recompiled. Interfaces get refactored in one repository while nobody knows which of the other fourteen depend on them.

These aren't edge cases - they're the specific failure modes that general-purpose AI doesn't know to look for, and that tired humans miss in review. These skills exist to catch them before they reach production.

In a system with dozens or hundreds of microservices, where AI is generating implementations faster than any team can meaningfully review, the question isn't whether bad code will be written - it's whether you'll catch it before it ships. Most teams don't have the bandwidth or the .NET depth to review every generated service thoroughly. These skills fill that gap.

---

## Install

Pick your tool. Claude Code, OpenCode and Codex each get a plugin with the skills, the multi-agent review, and the Synopsis dependency graph. Any other tool that supports [Agent Skills](https://agentskills.io) can use the skills on their own.

| What you get | Claude Code | OpenCode | Codex | Skills only |
|---|---|---|---|---|
| The 11 skills | ✓ | ✓ | ✓ | ✓ |
| Review with up to 5 reviewers + a reviewer that challenges them | `/dotnet-review` | `/dotnet-review` | ask for it (a skill) | one-pass review skill instead |
| Synopsis dependency graph, started for you | ✓ | ✓ | ✓ | start it yourself, or use the CLI |
| Story QA against the spec (per-AC verdicts, reuse, dead code) | `/dotnet-qa` | `/dotnet-qa` | ask for it (a skill) | one-pass QA skill instead |
| Phase-gated refactor loop with a design approval gate | `/dotnet-refactor` | `/dotnet-refactor` | ask for it (a skill) | — |
| Worker agents cannot change files | ✓ | ✓ | ✓ | — |
| A stronger model for big changes | automatic | opt-in | opt-in | — |
| Experimental log monitor | ✓ | — | — | — |
| Extra step after installing | none | none | one script | copy the skills |

How to use the three pipelines, in plain words: [docs/using-the-pipelines.md](docs/using-the-pipelines.md). Per-tool details: [docs/tool-compatibility.md](docs/tool-compatibility.md). Planned work: [docs/roadmap.md](docs/roadmap.md).

### Option 1: Plugin (Claude Code)

One command - skills activate automatically, and the review command, MCP server, and monitor come with it:

```text
/plugin marketplace add Metalnib/dotnet-episteme-skills
```

> **Windows:** the Synopsis MCP auto-start and log monitor launch through a POSIX shell script, so run Claude Code under **WSL2** to get them (Synopsis then runs as a normal Linux binary). The review command, agents, and skills work on native Windows regardless; native Windows can also drive Synopsis through the CLI via `skills/dotnet-techne-synopsis/scripts/detect-tool.ps1`.

### Option 2: Plugin (OpenCode)

OpenCode cannot read Claude Code plugins, so it has its own. Clone and run the installer, which registers everything and checks it worked:

```bash
git clone https://github.com/Metalnib/dotnet-episteme-skills.git
cd dotnet-episteme-skills
scripts/install-opencode.sh
```

You get the 11 skills, `/dotnet-review` with its six reviewers, `/dotnet-qa`, `/dotnet-refactor`, and the Synopsis graph server. `git pull` updates all of it. More: [docs/opencode-setup.md](docs/opencode-setup.md).

> Don't copy `agents/review/*.md` into OpenCode's own agent folder - they are written for Claude Code and OpenCode rejects them in a way that breaks its config. The installer converts them for you.

### Option 3: Plugin (Codex)

Two commands install the plugin, then one script adds the thirteen worker roles (Codex only accepts those from its own config):

```bash
codex plugin marketplace add Metalnib/dotnet-episteme-skills
codex plugin add dotnet-episteme-skills@dotnet-episteme-marketplace
scripts/install-codex.sh    # or from the installed copy under ~/.codex/plugins/cache/
```

You get the 11 skills, thirteen worker roles that cannot change files, and the Synopsis graph server. The pipelines ship as skills (Codex is deprecating custom slash prompts in favour of skills), so you ask for a multi-agent review, a story QA, or a design loop in your own words. More: [docs/codex-setup.md](docs/codex-setup.md).

> On first start Codex asks to trust the plugin's hook - choose *Trust all and continue*.
>
> A full review uses six agents. On a free or metered plan, ask for a normal review of small changes instead.

### Option 4: Skills-only (portable)

Download the latest archive from the [releases page](https://github.com/Metalnib/dotnet-episteme-skills/releases), extract, and copy `skills/` into your tool's skills directory:

```bash
# Linux / macOS
tar -xzf dotnet-episteme-skills-1.8.1.tar.gz
cp -R skills/* ~/.claude/skills/          # Claude Code (skills only, no plugin extras)
cp -R skills/* ~/.config/opencode/skill/  # OpenCode
cp -R skills/* ~/.agents/skills/          # OpenAI Codex
# pi: add the extracted skills/ directory to pi's skill paths in settings
```

```powershell
# Windows
Expand-Archive dotnet-episteme-skills-1.8.1.zip .
Copy-Item -Recurse skills\* "$env:USERPROFILE\.claude\skills\"
```

Or clone for easy updates:

```bash
git clone https://github.com/Metalnib/dotnet-episteme-skills.git
cp -R dotnet-episteme-skills/skills/* ~/.claude/skills/
# update later: git -C dotnet-episteme-skills pull && cp -R dotnet-episteme-skills/skills/* ~/.claude/skills/
```

### Recommended companion: C# language intelligence (Claude Code)

For real-time diagnostics and go-to-definition while reviewing or refactoring, install the official C# LSP plugin alongside this one - it complements the review skills and Synopsis (per-file code intelligence vs cross-repo architecture graph):

```text
/plugin install csharp-lsp@claude-plugins-official
```

```bash
dotnet tool install -g csharp-ls   # the language server binary the plugin expects on PATH
```

This plugin deliberately ships no `.lsp.json` of its own: only the first registered LSP server per file extension runs, so bundling one would conflict with the official plugin.

---

## Skills

| Skill | What it does | When to use it |
|---|---|---|
| `dotnet-techne-code-review` | Production-readiness review of .NET code: correctness, security, performance, data access, messaging, observability | After every AI-generated implementation, before any PR |
| `dotnet-techne-synopsis` | Roslyn-based dependency graph across all your repos: blast radius, cross-service call map, EF Core lineage, breaking-change diff | "What breaks if I change this?" across a microservices landscape |
| `dotnet-techne-inspect` | Decompiles and surfaces the public API of any NuGet package - internal or third-party | Before writing code against a package where you don't have the source |
| `dotnet-techne-story-qa` | Verifies a story implementation against its spec: per-AC verdicts with evidence and proving tests, reuse/design conformance, dead code, stale docs | Before marking a story done - "did we build what the ticket asked for?" |
| `dotnet-techne-crap-analysis` | Finds high-complexity, low-coverage methods (CRAP score) | Deciding where to focus test effort in a large codebase |
| `dotnet-techne-cross-repo-impact` | Checks a PR for breaking changes that hit other repos: API, DTO, endpoint, EF entity, NuGet package - and whether a compatible downstream PR already exists | Before merging a change to anything other services consume |
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

With the plugin installed, `/dotnet-review [target] [--cynical] [--model tier]` (fully: `/dotnet-episteme-skills:dotnet-review`) upgrades this to a multi-agent pipeline. Up to five reviewers run in parallel, each in a fresh isolated context: correctness/API, performance/AOT, security/observability, data/messaging/HTTP-integration, plus a generalist that hunts what falls between the lanes. Then an adversarial maintainer re-verifies every finding and refutes the ones that don't survive scrutiny, per [maintainer-playbook.md](skills/dotnet-techne-code-review/references/maintainer-playbook.md).

The orchestration is a bundled [dynamic workflow](workflows/dotnet-review.js): a deterministic script that scales the reviewer model to the size of the change and keeps raw findings out of your conversation. It runs slower than the single-context skill path - isolated reviewers and adversarial verification buy more thorough, run-to-run-consistent results in exchange for the time. `bash scripts/install-workflow.sh` optionally installs it as a native `/dotnet-review` command. On other tools the same playbook runs as a single-context falsification pass.

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

All commands support `--json`: `{"command":"...","ok":true,"result":{...},"ms":142,"schemaVersion":1}`. `schemaVersion` identifies the envelope contract and bumps only on a breaking shape change.

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
synopsis mcp (--root <rootPath> | --graph <graph.json>) [--socket <path> | --tcp <addr>] [--state-dir <path>] [--log-file <path>]
```

On Claude Code the plugin starts the MCP server automatically (macOS/Linux, and Windows under WSL2; `bin/synopsis-mcp-launcher.sh` picks the platform binary): the tools below are available in every session as `mcp__plugin_dotnet-episteme-skills_synopsis__<tool>`, with graph state persisted under the plugin data directory and diagnostics logged to `synopsis.log` there (`--log-file`, v1.6.0+). Servers aren't restarted automatically if they exit - use `/mcp` to reconnect.

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

## Story QA - did we build what the ticket asked for?

Code review hunts defects in a diff. The `dotnet-techne-story-qa` skill asks the other question: does the implementation match its **spec**? It finds the spec itself (an explicit path, the ticket key in your branch name, plan or design files in the repo) and asks you when nothing turns up. Skipping the spec is always your explicit choice, never a silent fallback.

You get one verdict per acceptance criterion - IMPLEMENTED / PARTIAL / MISSING - with file:line evidence and the test that proves it. Alongside that: reinvented helpers, drift from the project's patterns, dead code, stale docs and log texts, and comment discipline. The result is a FAIL / CONCERNS / PASS gate, persisted to `.episteme/QA-<slug>.md`. Comment findings carry their exact replacement line, and nothing is applied without your confirmation.

`/dotnet-qa [ticket|branch] [--spec path] [--base branch]` runs it as a pipeline on Claude Code and OpenCode (on Codex you ask for it): three lanes in parallel, then the adversarial maintainer from the review pipeline falsifies the findings and challenges acceptance verdicts whose evidence does not hold, so a false "IMPLEMENTED" cannot pass unexamined.

```
QA this branch against YB-13323.

Verify this story's implementation against the acceptance criteria.

Did we cover all ACs of the spec in docs/specs/payment-retry.md? Anything dead left behind?
```

---

## Refactor loop - design before code, with gates

`/dotnet-refactor` is a phase-gated design loop for non-trivial refactoring (Claude Code and OpenCode; on Codex you ask for it). Session-blind workers map the complete branch/consumer/sibling set and walk every dataflow path under failure conditions before any design exists. Probes verify vendor behaviour instead of assuming it. Then the design gate stops for your approval, before a single line changes.

After approval the design is frozen. If it has to change mid-flight, a conformance auditor re-checks the entire branch against the new design - the author never grades their own homework. Out-of-scope findings go to an append-only Deferred ledger instead of widening the branch. State lives in `.episteme/DESIGN-<slug>.md`, so the loop survives `/clear`, compaction and fresh sessions.

Small target? `--lite` runs one combined map-and-trace worker instead of the full fan-out, and escalates honestly when the area turns out bigger than one pass should carry.

A plain-words walkthrough of all three pipelines: [docs/using-the-pipelines.md](docs/using-the-pipelines.md).

---

## Other skills

These skills handle specific .NET engineering concerns. Each activates automatically when you describe the relevant problem.

| Skill | Best for |
|---|---|
| `dotnet-techne-crap-analysis` | "Which methods are highest risk (high complexity, low coverage)?" Prioritises where to focus test effort. |
| `dotnet-techne-cross-repo-impact` | "Does this PR break another repo?" Cross-repo API, DTO, endpoint, entity and package breaks, plus compatible-PR search. |
| `dotnet-techne-csharp-api-design` | Designing a new public API or evolving an existing one without breaking callers. Covers versioning, deprecation, compatibility shims. |
| `dotnet-techne-csharp-coding-standards` | "Is this idiomatic modern C#?" Refactoring guidance, pattern choices, maintainability. |
| `dotnet-techne-csharp-concurrency-patterns` | Choosing between async/await, `Channel<T>`, `IAsyncEnumerable`, Dataflow, Rx. Avoiding lock contention and deadlocks. |
| `dotnet-techne-csharp-type-design-performance` | Hot-path type decisions: `readonly struct` vs class, `FrozenDictionary`, `Span<T>`, sealed types, allocation profiling. |
| `dotnet-techne-serialisation` | Picking a serialisation format and keeping wire contracts backwards-compatible across services and versions. |

---

## Advanced / Contributing

### Repository layout

```
skills/                  One folder per skill, each with SKILL.md (portable, Agent Skills spec)
agents/                  Claude Code plugin subagents (review, refactor, and qa lanes)
commands/                Claude Code slash commands (/dotnet-review, /dotnet-qa, /dotnet-refactor)
workflows/               Bundled dynamic workflow scripts for the three pipelines
hooks/                   PreToolUse git guard + refactor-loop state reload
bin/                     Plugin launcher scripts (Synopsis MCP)
monitors/                Experimental Claude Code background monitors
docs/                    Pipeline usage guide, per-tool setup and compatibility, worker restrictions
src/synopsis/            Synopsis source (.NET 10)
  Synopsis.Analysis/     Roslyn analysis, graph model, querying
  Synopsis/              CLI, MCP server, JSON output
  Synopsis.Tests/        161 unit tests
scripts/                 Validation and CI helpers
.mcp.json                Plugin MCP server declaration (Synopsis)
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

1. Update `CHANGELOG.md` - the new section header must be `## [<version>] — <date>`. The release workflow extracts notes by matching that exact header, so a header like `## [Unreleased]` ships a release with empty notes.
2. Sync all four version fields: `.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json`, `.codex-plugin/plugin.json`, `package.json`. `scripts/validate.sh` fails on drift between the last three, `scripts/validate-marketplace.sh` on the marketplace, and CI checks the tag against `plugin.json` and `package.json`.
3. Bump `<Version>` in `src/synopsis/Directory.Build.props` **only when Synopsis itself changed** - the skills advertise `Requires Synopsis v1.6.0+`, and nothing forces the binary to track the plugin version.
4. Validate locally:
   ```bash
   bash scripts/validate.sh && bash scripts/validate-marketplace.sh
   bash scripts/test-guard.sh && node scripts/test-opencode-plugin.mjs
   ```
5. Tag and push - CI builds all 6 binaries, runs the test suite, and publishes the GitHub release:
   ```bash
   git tag v1.8.1
   git push origin main v1.8.1
   ```
6. After the release, check the install paths once: `/plugin marketplace add Metalnib/dotnet-episteme-skills` (Claude Code), `codex plugin marketplace add Metalnib/dotnet-episteme-skills` (Codex), and `scripts/install-opencode.sh` from a fresh clone (OpenCode).

npm publishing is deferred to 2.0 ([roadmap](docs/roadmap.md)). The `publish-npm` job stays dormant until an `NPM_TOKEN` repository secret exists, so releases are unaffected.

---

## Specification references

- Agent Skills spec: https://agentskills.io/specification
- Claude Code skills: https://code.claude.com/docs/en/skills
- OpenCode skills: https://opencode.ai/docs/skills

## License

MIT
