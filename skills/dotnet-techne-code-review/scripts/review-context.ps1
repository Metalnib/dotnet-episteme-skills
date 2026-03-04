# Generate full review context for AI code review
# Usage: .\review-context.ps1 [-Target main] [-Mode brief|full]

param(
    [string]$Target = "main",
    [ValidateSet("brief", "full")]
    [string]$Mode = "brief"
)

$ErrorActionPreference = "Stop"

Write-Host "# Code Review Context"
Write-Host "Generated: $(Get-Date -Format 'o')"
Write-Host "Target: $Target"
Write-Host ""

# Get changed files
if ($Target -match '\.\.') {
    $changedFiles = git diff --name-only $Target | Where-Object { $_ -match '\.cs$' }
    $diffCmd = "git diff $Target"
    $mergeBase = $Target.Split('..')[0]
}
else {
    $mergeBase = git merge-base HEAD $Target 2>$null
    if (-not $mergeBase) { $mergeBase = $Target }
    $changedFiles = git diff --name-only "$mergeBase...HEAD" | Where-Object { $_ -match '\.cs$' }
    $diffCmd = "git diff $mergeBase...HEAD"
}

if (-not $changedFiles) {
    Write-Host "No C# files changed."
    exit 0
}

$fileCount = @($changedFiles).Count
Write-Host "## Changed C# Files ($fileCount)"
Write-Host '```'
$changedFiles | ForEach-Object { Write-Host $_ }
Write-Host '```'
Write-Host ""

# Commits in range
Write-Host "## Commits"
Write-Host '```'
if ($Target -match '\.\.') {
    git log --oneline $Target
}
else {
    git log --oneline "$mergeBase...HEAD" 2>$null
    if ($LASTEXITCODE -ne 0) { git log --oneline -10 }
}
Write-Host '```'
Write-Host ""

# Changed .csproj files
$changedProj = git diff --name-only "$mergeBase...HEAD" 2>$null | Where-Object { $_ -match '\.csproj$' }
if ($changedProj) {
    Write-Host "## Changed Project Files"
    Write-Host '```'
    $changedProj | ForEach-Object { Write-Host $_ }
    Write-Host '```'
    Write-Host ""
}

if ($Mode -eq "full") {
    Write-Host "## Full Diff"
    Write-Host '```diff'
    Invoke-Expression "$diffCmd -- '*.cs'" 2>$null
    Write-Host '```'
    Write-Host ""
    
    Write-Host "## File Contents (changed files)"
    foreach ($file in $changedFiles) {
        if (Test-Path $file) {
            Write-Host "### $file"
            Write-Host '```csharp'
            Get-Content $file
            Write-Host '```'
            Write-Host ""
        }
    }
}
else {
    Write-Host "## Diff Stats"
    Write-Host '```'
    Invoke-Expression "$diffCmd --stat -- '*.cs'" 2>$null
    Write-Host '```'
    Write-Host ""
    
    Write-Host "## Changed Methods/Classes (signatures)"
    foreach ($file in $changedFiles) {
        if (Test-Path $file) {
            Write-Host "### $file"
            Write-Host '```'
            $signatures = Get-Content $file | Select-String -Pattern '^\s*(public|private|protected|internal).*\s+(class|interface|record|struct|enum|void|async|Task|string|int|bool|var)\s+\w+' | Select-Object -First 30
            if ($signatures) {
                $signatures | ForEach-Object { Write-Host $_.Line.Trim() }
            }
            else {
                Write-Host "(no public members found)"
            }
            Write-Host '```'
            Write-Host ""
        }
    }
}

Write-Host "---"
Write-Host "Use -Mode full for complete file contents and diff."
