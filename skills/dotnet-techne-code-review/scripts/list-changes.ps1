# List all changed files with categorization for C# projects
# Usage: .\list-changes.ps1 [-Range main|HEAD~3..HEAD]

param(
    [string]$Range = ""
)

$ErrorActionPreference = "Stop"

function Get-ChangedFiles {
    if ([string]::IsNullOrEmpty($Range)) {
        # Uncommitted changes
        $staged = git diff --name-only --cached 2>$null
        $unstaged = git diff --name-only HEAD 2>$null
        return ($staged + $unstaged) | Sort-Object -Unique
    }
    elseif ($Range -match '\.\.') {
        # Explicit range
        return git diff --name-only $Range
    }
    else {
        # Branch comparison
        $mergeBase = git merge-base HEAD $Range 2>$null
        if (-not $mergeBase) { $mergeBase = $Range }
        return git diff --name-only "$mergeBase...HEAD"
    }
}

$files = Get-ChangedFiles | Where-Object { $_ }

Write-Host "=== Changed Files Summary ===" -ForegroundColor Cyan
Write-Host ""

# C# source files
$csFiles = $files | Where-Object { $_ -match '\.cs$' }
if ($csFiles) {
    Write-Host "## C# Source Files ($($csFiles.Count))" -ForegroundColor Green
    $csFiles | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

# Project files
$projFiles = $files | Where-Object { $_ -match '\.(csproj|sln|props|targets)$' }
if ($projFiles) {
    Write-Host "## Project/Build Files ($($projFiles.Count))" -ForegroundColor Green
    $projFiles | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

# Config files
$configFiles = $files | Where-Object { $_ -match '\.(json|xml|yaml|yml|config)$' }
if ($configFiles) {
    Write-Host "## Config Files ($($configFiles.Count))" -ForegroundColor Green
    $configFiles | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

# Migrations
$migrationFiles = $files | Where-Object { $_ -match 'migration' }
if ($migrationFiles) {
    Write-Host "## Database Migrations ($($migrationFiles.Count))" -ForegroundColor Yellow
    $migrationFiles | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

# Test files
$testFiles = $files | Where-Object { $_ -match '(test|spec)' }
if ($testFiles) {
    Write-Host "## Test Files ($($testFiles.Count))" -ForegroundColor Magenta
    $testFiles | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

# Other files
$otherFiles = $files | Where-Object { 
    $_ -notmatch '\.(cs|csproj|sln|props|targets|json|xml|yaml|yml|config)$' -and
    $_ -notmatch '(test|spec|migration)'
}
if ($otherFiles) {
    Write-Host "## Other Files ($($otherFiles.Count))" -ForegroundColor Gray
    $otherFiles | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

Write-Host "=== Total: $($files.Count) files ===" -ForegroundColor Cyan
