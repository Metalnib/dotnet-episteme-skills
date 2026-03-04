# Get all changes between current branch and target branch (default: main)
# Usage: .\branch-diff.ps1 [-TargetBranch main] [-Mode stat|files|full|cs]

param(
    [string]$TargetBranch = "main",
    [ValidateSet("stat", "files", "full", "cs")]
    [string]$Mode = "stat"
)

$ErrorActionPreference = "Stop"

# Find merge base
$MergeBase = git merge-base HEAD $TargetBranch 2>$null
if (-not $MergeBase) { $MergeBase = $TargetBranch }

switch ($Mode) {
    "stat" {
        Write-Host "=== Branch diff stats vs $TargetBranch ===" -ForegroundColor Cyan
        git diff --stat "$MergeBase...HEAD"
    }
    "files" {
        Write-Host "=== Changed files vs $TargetBranch ===" -ForegroundColor Cyan
        git diff --name-only "$MergeBase...HEAD"
    }
    "full" {
        Write-Host "=== Full diff vs $TargetBranch ===" -ForegroundColor Cyan
        git diff "$MergeBase...HEAD"
    }
    "cs" {
        Write-Host "=== C# files changed vs $TargetBranch ===" -ForegroundColor Cyan
        $files = git diff --name-only "$MergeBase...HEAD" | Where-Object { $_ -match '\.(cs|csproj|sln)$' }
        if ($files) { $files } else { Write-Host "(no C# files changed)" }
    }
}
