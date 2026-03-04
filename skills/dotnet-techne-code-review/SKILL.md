---
name: code-review
description: End-to-end senior code review for .NET microservices and libraries (style, correctness, performance/low-GC/AOT, security, database/EF Core/PostgreSQL, messaging/RabbitMQ, logging/observability). Use when reviewing PRs, diffs, branches, or when asked to improve code quality and production readiness.
license: MIT
compatibility: Requires git and bash (Linux/macOS) or PowerShell (Windows). Works with any AI agent that supports the Agent Skills specification and has file read and command execution tools.
metadata:
  author: Hristo Georgiev
  organization: Wizer Technologies
  version: "2.0"
---

# End-to-end Code Review (Style + Perf + Security + DB + Messaging + Logging)

## Requirements

This skill requires:
- **Git** - for diff and commit analysis
- **Bash/Zsh** (Linux/macOS) or **PowerShell 5.1+** (Windows)
- **Agent tools**: file read capability and shell command execution

Optional but recommended:
- `rg` (ripgrep) - for faster dependency searches on Linux/macOS
- `dotnet` CLI - for build verification

## When to use this skill
Use this skill when the user asks for:
- PR/branch/diff review
- "code review this implementation"
- performance + low GC review
- AOT/trimming readiness review
- security review
- EF Core / PostgreSQL review
- RabbitMQ / messaging review
- logging/observability review

## Git Scripts (use these to gather context)

The skill includes helper scripts in `scripts/` directory. **Run these first to understand what changed.**

**Platform support:**
- **Linux/macOS:** Use `.sh` scripts with bash/zsh
- **Windows:** Use `.ps1` scripts with PowerShell

### 1. Branch comparison (vs main or target branch)

**Bash (Linux/macOS):**
```bash
./scripts/branch-diff.sh main --cs      # C# files changed
./scripts/branch-diff.sh main --stat    # Diff stats
./scripts/branch-diff.sh main --full    # Full diff
./scripts/branch-diff.sh develop --files
```

**PowerShell (Windows):**
```powershell
.\scripts\branch-diff.ps1 -TargetBranch main -Mode cs
.\scripts\branch-diff.ps1 -TargetBranch main -Mode stat
.\scripts\branch-diff.ps1 -TargetBranch main -Mode full
.\scripts\branch-diff.ps1 -TargetBranch develop -Mode files
```

### 2. Last N commits review

**Bash:**
```bash
./scripts/last-commits.sh 1 --files     # Files in last commit
./scripts/last-commits.sh 5 --stat      # Stats for last 5
./scripts/last-commits.sh 3 --cs        # C# files in last 3
./scripts/last-commits.sh 10 --log      # Commit log
```

**PowerShell:**
```powershell
.\scripts\last-commits.ps1 -N 1 -Mode files
.\scripts\last-commits.ps1 -N 5 -Mode stat
.\scripts\last-commits.ps1 -N 3 -Mode cs
.\scripts\last-commits.ps1 -N 10 -Mode log
```

### 3. Categorized file list

**Bash:**
```bash
./scripts/list-changes.sh main          # Changes vs main
./scripts/list-changes.sh               # Uncommitted changes
./scripts/list-changes.sh HEAD~5..HEAD  # Commit range
```

**PowerShell:**
```powershell
.\scripts\list-changes.ps1 -Range main
.\scripts\list-changes.ps1                          # Uncommitted
.\scripts\list-changes.ps1 -Range "HEAD~5..HEAD"
```

### 4. Find dependencies and usages

**Bash:**
```bash
./scripts/find-deps.sh UserService                    # Find type usages
./scripts/find-deps.sh src/Services/OrderService.cs   # Types in file
./scripts/find-deps.sh IMessagePublisher src/         # Search path
```

**PowerShell:**
```powershell
.\scripts\find-deps.ps1 -Target UserService
.\scripts\find-deps.ps1 -Target src/Services/OrderService.cs
.\scripts\find-deps.ps1 -Target IMessagePublisher -SearchPath src/
```

### 5. Project dependency analysis

**Bash:**
```bash
./scripts/project-deps.sh                           # All .csproj
./scripts/project-deps.sh src/MyApi/MyApi.csproj    # Specific project
./scripts/project-deps.sh src/                      # Directory
```

**PowerShell:**
```powershell
.\scripts\project-deps.ps1
.\scripts\project-deps.ps1 -Target src/MyApi/MyApi.csproj
.\scripts\project-deps.ps1 -Target src/
```

### 6. Generate full review context

**Bash:**
```bash
./scripts/review-context.sh main --brief            # Signatures/stats
./scripts/review-context.sh main --full             # Complete diff
./scripts/review-context.sh HEAD~3..HEAD --full     # Commit range
```

**PowerShell:**
```powershell
.\scripts\review-context.ps1 -Target main -Mode brief
.\scripts\review-context.ps1 -Target main -Mode full
.\scripts\review-context.ps1 -Target "HEAD~3..HEAD" -Mode full
```

### 7. Find affected tests

**Bash:**
```bash
./scripts/affected-tests.sh main          # Branch changes
./scripts/affected-tests.sh HEAD~5..HEAD  # Recent commits
```

**PowerShell:**
```powershell
.\scripts\affected-tests.ps1 -Target main
.\scripts\affected-tests.ps1 -Target "HEAD~5..HEAD"
```

## Review Workflow

### Step 0: Gather context (ALWAYS DO THIS FIRST)

**Bash (Linux/macOS):**
```bash
./scripts/list-changes.sh main           # 1. Understand scope
./scripts/last-commits.sh 10 --log       # 2. Check commits
./scripts/review-context.sh main --brief # 3. Generate review context
./scripts/project-deps.sh                # 4. Check project dependencies
```

**PowerShell (Windows):**
```powershell
.\scripts\list-changes.ps1 -Range main
.\scripts\last-commits.ps1 -N 10 -Mode log
.\scripts\review-context.ps1 -Target main -Mode brief
.\scripts\project-deps.ps1
```

Then read the changed files using your file read tool (e.g., `read`, `cat`, `View`).

### Step 1: Establish component context
1. Identify the component type:
    - API controller / gRPC service / background worker / repository / domain library / messaging publisher/consumer
2. Identify the critical paths:
    - request handling path
    - message publish/consume path
    - DB read/write path
3. Identify invariants:
    - idempotency keys, correlation IDs, ordering requirements, transaction boundaries

### Step 2: Correctness + API design (blocking issues first)
Check for:
- CancellationToken propagation and correct `OperationCanceledException` handling
- exception safety (no swallowed exceptions without decision)
- thread-safety and lifetimes (singleton with mutable state, reuse of non-thread-safe types)
- deterministic behavior under retries and partial failures
- public API clarity:
    - naming, overloads, nullability, default values, backwards compatibility

### Step 3: Style + maintainability (Microsoft guidelines)
Check for:
- consistent naming, minimal cleverness
- small methods with clear responsibilities
- avoid unnecessary comments; prefer self-documenting code
- avoid duplicated logic; extract shared helpers where it reduces risk
- DI usage: interfaces over concretes, avoid service locator patterns unless justified

### Step 4: Performance + low GC + AOT/trimming

#### Hot-path allocation checklist
Flag:
- LINQ in hot paths
- closures and captures in loops
- `string` formatting/interpolation in tight loops
- `enum.ToString()` and boxing (prefer cached names or numeric)
- `DateTime.ToString(...)` in hot paths (prefer numeric timestamps or cached formatting strategy)
- per-call allocations for headers/properties/options objects

#### Concurrency / buffering checklist
- define backpressure (bounded queue/channel)
- avoid unbounded buffers unless explicitly required
- for `Channel<T>`:
    - set `SingleReader = true`/`SingleWriter = true` where applicable
    - choose full-mode behavior intentionally (wait, drop oldest, drop newest)

#### AOT/trimming checklist
- if JSON serialization is used in a library:
    - prefer source-gen (`JsonSerializerContext`/`JsonTypeInfo`) patterns when possible
    - if reflection-based JSON is supported, annotate public APIs with:
        - `[RequiresDynamicCode]`
        - `[RequiresUnreferencedCode]`
- if trimmer warnings appear:
    - fix root cause OR annotate/suppress with justification at the correct boundary

### Step 5: Logging + observability
Check for:
- structured logs with stable identifiers: `EventId`, `CorrelationId`, `CausationId`, user/account IDs (avoid PII)
- `LoggerMessage` source generators for hot paths
- correct log levels:
    - noisy retry loops should avoid high-frequency warning spam
- add metrics suggestions when useful:
    - publish latency, retries count, buffer depth, DB call duration

### Step 6: Security review
Check for:
- no secrets in code/logs
- input validation (especially webhooks)
- authentication/authorization boundaries are explicit
- safe serialization (avoid dangerous polymorphic deserialization defaults)
- SSRF risks for outbound HTTP calls (validate/allow-list)
- least privilege assumptions for DB and broker credentials

### Step 7: Database / EF Core / PostgreSQL review

Use dependency scripts to find all database contexts and usages:
- **Bash:** `./scripts/find-deps.sh DbContext`
- **PowerShell:** `.\scripts\find-deps.ps1 -Target DbContext`

Check for:
- correct tracking usage (`AsNoTracking` where appropriate)
- N+1 query patterns
- missing indexes implied by query patterns
- transaction boundaries aligned with business invariants
- concurrency tokens / unique constraints for idempotency
- connection pooling and proper async calls
- avoid large materializations; prefer projection and pagination

### Step 8: Messaging / RabbitMQ review

Use dependency scripts to find messaging code:
- **Bash:** `./scripts/find-deps.sh IMessagePublisher` or `./scripts/find-deps.sh IChannel`
- **PowerShell:** `.\scripts\find-deps.ps1 -Target IMessagePublisher`

Check for:
- connection/channel lifecycle correctness and cleanup on shutdown
- publisher confirms (when reliability is required)
- retry policy and classification of retryable exceptions
- topology declarations strategy (exchange/queue/bindings)
- mandatory publish + returned message handling expectations
- backpressure policy when buffer is full (drop vs block vs fail-fast)

## Dependency Tracing

When reviewing a change, trace its impact:

**Bash (Linux/macOS):**
```bash
./scripts/find-deps.sh ChangedClass.cs      # 1. Find dependencies
./scripts/find-deps.sh IChangedInterface    # 2. Check callers
./scripts/affected-tests.sh main            # 3. Verify test coverage
```

**PowerShell (Windows):**
```powershell
.\scripts\find-deps.ps1 -Target ChangedClass.cs
.\scripts\find-deps.ps1 -Target IChangedInterface
.\scripts\affected-tests.ps1 -Target main
```

## Output format (use this structure exactly)

### Summary
- What changed / what you reviewed
- Overall risk level: Low / Medium / High

### Findings
Provide a list of findings; each finding must include:
- **Severity:** blocking / important / suggestion
- **Area:** correctness | style | performance | AOT | security | DB | messaging | logging
- **Location:** file + type/method
- **Impact:** what can break (prod failure mode)
- **Fix:** concrete recommendation (include minimal patch guidance when possible)

### Quick wins (top 3)
- The three highest ROI changes the team can do immediately

### Follow-ups (optional)
- Larger refactors or architectural changes (only if necessary)

## Quick Reference Commands

### Bash (Linux/macOS)
```bash
# === Branch review ===
./scripts/branch-diff.sh main --cs        # C# files changed
./scripts/branch-diff.sh main --full      # Full diff
./scripts/list-changes.sh main            # Categorized list

# === Commit review ===
./scripts/last-commits.sh 1 --full        # Last commit diff
./scripts/last-commits.sh 5 --cs          # C# files in last 5

# === Dependencies ===
./scripts/find-deps.sh TypeName           # Find usages
./scripts/project-deps.sh                 # Project references

# === Full context ===
./scripts/review-context.sh main --full   # Everything for AI review
./scripts/affected-tests.sh main          # Related tests
```

### PowerShell (Windows)
```powershell
# === Branch review ===
.\scripts\branch-diff.ps1 -TargetBranch main -Mode cs
.\scripts\branch-diff.ps1 -TargetBranch main -Mode full
.\scripts\list-changes.ps1 -Range main

# === Commit review ===
.\scripts\last-commits.ps1 -N 1 -Mode full
.\scripts\last-commits.ps1 -N 5 -Mode cs

# === Dependencies ===
.\scripts\find-deps.ps1 -Target TypeName
.\scripts\project-deps.ps1

# === Full context ===
.\scripts\review-context.ps1 -Target main -Mode full
.\scripts\affected-tests.ps1 -Target main
```
