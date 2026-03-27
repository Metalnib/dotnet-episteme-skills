# Find test files that may be affected by changes
# Usage: .\affected-tests.ps1 [-Target main]

param(
    [string]$Target = "main"
)

$ErrorActionPreference = "Stop"

# Get changed files
if ($Target -match '\.\.') {
    $changedFiles = git --no-pager diff --name-only $Target | Where-Object { $_ -match '\.cs$' }
}
else {
    $mergeBase = git --no-pager merge-base HEAD $Target 2>$null
    if (-not $mergeBase) { $mergeBase = $Target }
    $changedFiles = git --no-pager diff --name-only "$mergeBase...HEAD" | Where-Object { $_ -match '\.cs$' }
}

if (-not $changedFiles) {
    Write-Host "No C# files changed."
    exit 0
}

Write-Host "=== Changed Source Files ===" -ForegroundColor Cyan
$sourceFiles = $changedFiles | Where-Object { $_ -notmatch '(test|spec)' }
if ($sourceFiles) {
    $sourceFiles | ForEach-Object { Write-Host "  $_" }
}
else {
    Write-Host "  (none)"
}
Write-Host ""

Write-Host "=== Changed Test Files ===" -ForegroundColor Cyan
$testFiles = $changedFiles | Where-Object { $_ -match '(test|spec)' }
if ($testFiles) {
    $testFiles | ForEach-Object { Write-Host "  $_" }
}
else {
    Write-Host "  (none)"
}
Write-Host ""

Write-Host "=== Potentially Affected Tests ===" -ForegroundColor Cyan

foreach ($file in $sourceFiles) {
    if (-not $file) { continue }
    
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($file)
    
    # Search for test files matching this name
    $matchingTests = Get-ChildItem -Path . -Filter "*.cs" -Recurse -File | Where-Object {
        $_.Name -match "${baseName}.*Test" -or 
        $_.Name -match "${baseName}.*Spec" -or
        $_.Name -match "Test.*${baseName}"
    } | Select-Object -First 10
    
    if ($matchingTests) {
        Write-Host "  ${file}:" -ForegroundColor Yellow
        $matchingTests | ForEach-Object { Write-Host "    -> $($_.FullName)" }
    }
}
Write-Host ""

Write-Host "=== Test Projects ===" -ForegroundColor Cyan
$testProjects = Get-ChildItem -Path . -Filter "*.csproj" -Recurse -File | Where-Object {
    $content = Get-Content $_.FullName -Raw
    $content -match 'Microsoft\.NET\.Test\.Sdk|xunit|NUnit|MSTest'
}

if ($testProjects) {
    $testProjects | ForEach-Object { Write-Host "  $($_.FullName)" }
}
else {
    Write-Host "  (none found)"
}
