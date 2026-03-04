# Get changes from last N commits
# Usage: .\last-commits.ps1 [-N 1] [-Mode stat|files|full|log|cs]

param(
    [int]$N = 1,
    [ValidateSet("stat", "files", "full", "log", "cs")]
    [string]$Mode = "stat"
)

$ErrorActionPreference = "Stop"

switch ($Mode) {
    "stat" {
        Write-Host "=== Stats for last $N commit(s) ===" -ForegroundColor Cyan
        git diff --stat "HEAD~$N..HEAD"
    }
    "files" {
        Write-Host "=== Files changed in last $N commit(s) ===" -ForegroundColor Cyan
        git diff --name-only "HEAD~$N..HEAD"
    }
    "full" {
        Write-Host "=== Full diff for last $N commit(s) ===" -ForegroundColor Cyan
        git diff "HEAD~$N..HEAD"
    }
    "log" {
        Write-Host "=== Log for last $N commit(s) ===" -ForegroundColor Cyan
        git log --oneline -n $N
        Write-Host ""
        Write-Host "=== Detailed log ===" -ForegroundColor Cyan
        git log --stat -n $N
    }
    "cs" {
        Write-Host "=== C# files changed in last $N commit(s) ===" -ForegroundColor Cyan
        $files = git diff --name-only "HEAD~$N..HEAD" | Where-Object { $_ -match '\.(cs|csproj|sln)$' }
        if ($files) { $files } else { Write-Host "(no C# files changed)" }
    }
}
