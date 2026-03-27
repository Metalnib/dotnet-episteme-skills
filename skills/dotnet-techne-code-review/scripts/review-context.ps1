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
    $rangeSpec = $Target
}

$diffFileList = git --no-pager diff --name-only $rangeSpec 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Invalid review target: $Target" -ForegroundColor Red
    exit 1
}

$changedFiles = $diffFileList | Where-Object { $_ -match '\.cs$' }
else {
    $mergeBase = git --no-pager merge-base HEAD $Target 2>$null
    if (-not $mergeBase) { $mergeBase = $Target }
    $rangeSpec = "$mergeBase...HEAD"
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
    git --no-pager log --oneline $Target
}
else {
    git --no-pager log --oneline $rangeSpec 2>$null
    if ($LASTEXITCODE -ne 0) { git --no-pager log --oneline -10 }
}
Write-Host '```'
Write-Host ""

# Changed .csproj files
$changedProj = git --no-pager diff --name-only $rangeSpec 2>$null | Where-Object { $_ -match '\.csproj$' }
if ($changedProj) {
    Write-Host "## Changed Project Files"
    Write-Host '```'
    $changedProj | ForEach-Object { Write-Host $_ }
    Write-Host '```'
    Write-Host ""
}

Write-Host "## Potential Risk Signals (heuristic)"
Write-Host '```'
$riskPattern = 'TODO|FIXME|HACK|throw new NotImplementedException|catch\s*\(Exception|\.Result\b|\.Wait\(|#pragma warning disable|AllowAnonymous|FromSqlRaw|ExecuteSqlRaw|IgnoreQueryFilters|Task\.Run\('
$riskSignals = git --no-pager diff $rangeSpec -- '*.cs' 2>$null | Select-String -Pattern $riskPattern | Select-Object -First 80
if ($riskSignals) {
    $riskSignals | ForEach-Object { Write-Host $_.Line.Trim() }
}
else {
    Write-Host "(no heuristic risk signals matched)"
}
Write-Host '```'
Write-Host ""

if ($Mode -eq "full") {
    Write-Host "## Full Diff"
    Write-Host '```diff'
    git --no-pager diff $rangeSpec -- '*.cs' 2>$null
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
    git --no-pager diff --stat $rangeSpec -- '*.cs' 2>$null
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
