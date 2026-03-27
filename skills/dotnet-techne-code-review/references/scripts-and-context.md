# Scripts and Context Gathering
## Contents
- [Baseline context commands (run first)](#baseline-context-commands-run-first)
- [Script catalog](#script-catalog)
  - [Branch comparison](#branch-comparison)
  - [Last N commits](#last-n-commits)
  - [Categorized file list](#categorized-file-list)
  - [Dependency and usage search](#dependency-and-usage-search)
  - [Project dependency analysis](#project-dependency-analysis)
  - [Full review context](#full-review-context)
  - [Affected tests](#affected-tests)
- [Dependency tracing workflow](#dependency-tracing-workflow)

## Baseline context commands (run first)
### Bash (Linux/macOS)
```bash
./scripts/list-changes.sh main
./scripts/last-commits.sh 10 --log
./scripts/review-context.sh main --brief
./scripts/project-deps.sh
```
### PowerShell (Windows)
```powershell
.\scripts\list-changes.ps1 -Range main
.\scripts\last-commits.ps1 -N 10 -Mode log
.\scripts\review-context.ps1 -Target main -Mode brief
.\scripts\project-deps.ps1
```
Then read changed files with your file-read tool.

## Script catalog
### Branch comparison
#### Bash
```bash
./scripts/branch-diff.sh main --cs
./scripts/branch-diff.sh main --stat
./scripts/branch-diff.sh main --full
./scripts/branch-diff.sh develop --files
```
#### PowerShell
```powershell
.\scripts\branch-diff.ps1 -TargetBranch main -Mode cs
.\scripts\branch-diff.ps1 -TargetBranch main -Mode stat
.\scripts\branch-diff.ps1 -TargetBranch main -Mode full
.\scripts\branch-diff.ps1 -TargetBranch develop -Mode files
```

### Last N commits
#### Bash
```bash
./scripts/last-commits.sh 1 --files
./scripts/last-commits.sh 5 --stat
./scripts/last-commits.sh 3 --cs
./scripts/last-commits.sh 10 --log
```
#### PowerShell
```powershell
.\scripts\last-commits.ps1 -N 1 -Mode files
.\scripts\last-commits.ps1 -N 5 -Mode stat
.\scripts\last-commits.ps1 -N 3 -Mode cs
.\scripts\last-commits.ps1 -N 10 -Mode log
```

### Categorized file list
#### Bash
```bash
./scripts/list-changes.sh main
./scripts/list-changes.sh
./scripts/list-changes.sh HEAD~5..HEAD
```
#### PowerShell
```powershell
.\scripts\list-changes.ps1 -Range main
.\scripts\list-changes.ps1
.\scripts\list-changes.ps1 -Range "HEAD~5..HEAD"
```

### Dependency and usage search
#### Bash
```bash
./scripts/find-deps.sh UserService
./scripts/find-deps.sh src/Services/OrderService.cs
./scripts/find-deps.sh IMessagePublisher src/
```
#### PowerShell
```powershell
.\scripts\find-deps.ps1 -Target UserService
.\scripts\find-deps.ps1 -Target src/Services/OrderService.cs
.\scripts\find-deps.ps1 -Target IMessagePublisher -SearchPath src/
```

### Project dependency analysis
#### Bash
```bash
./scripts/project-deps.sh
./scripts/project-deps.sh src/MyApi/MyApi.csproj
./scripts/project-deps.sh src/
```
#### PowerShell
```powershell
.\scripts\project-deps.ps1
.\scripts\project-deps.ps1 -Target src/MyApi/MyApi.csproj
.\scripts\project-deps.ps1 -Target src/
```

### Full review context
#### Bash
```bash
./scripts/review-context.sh main --brief
./scripts/review-context.sh main --full
./scripts/review-context.sh HEAD~3..HEAD --full
```
#### PowerShell
```powershell
.\scripts\review-context.ps1 -Target main -Mode brief
.\scripts\review-context.ps1 -Target main -Mode full
.\scripts\review-context.ps1 -Target "HEAD~3..HEAD" -Mode full
```

### Affected tests
#### Bash
```bash
./scripts/affected-tests.sh main
./scripts/affected-tests.sh HEAD~5..HEAD
```
#### PowerShell
```powershell
.\scripts\affected-tests.ps1 -Target main
.\scripts\affected-tests.ps1 -Target "HEAD~5..HEAD"
```

## Dependency tracing workflow
### Bash
```bash
./scripts/find-deps.sh ChangedClass.cs
./scripts/find-deps.sh IChangedInterface
./scripts/affected-tests.sh main
```
### PowerShell
```powershell
.\scripts\find-deps.ps1 -Target ChangedClass.cs
.\scripts\find-deps.ps1 -Target IChangedInterface
.\scripts\affected-tests.ps1 -Target main
```
