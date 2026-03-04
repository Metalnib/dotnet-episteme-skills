# Find dependencies and usages for a C# type/file
# Usage: .\find-deps.ps1 -Target <file-or-type> [-SearchPath .]

param(
    [Parameter(Mandatory=$true)]
    [string]$Target,
    [string]$SearchPath = "."
)

$ErrorActionPreference = "Stop"

$types = @()

# Extract type name from file path if given
if ($Target -match '\.cs$') {
    if (Test-Path $Target) {
        Write-Host "=== Types defined in $Target ===" -ForegroundColor Cyan
        $content = Get-Content $Target -Raw
        $matches = [regex]::Matches($content, '(class|interface|record|struct|enum)\s+(\w+)')
        foreach ($m in $matches) {
            Write-Host "  $($m.Value)"
            $types += $m.Groups[2].Value
        }
        Write-Host ""
    }
    else {
        Write-Host "File not found: $Target" -ForegroundColor Red
        exit 1
    }
}
else {
    $types = @($Target)
}

if ($types.Count -eq 0) {
    Write-Host "No types found in $Target" -ForegroundColor Red
    exit 1
}

$pattern = $types -join '|'

Write-Host "=== Files referencing: $($types -join ', ') ===" -ForegroundColor Cyan

# Use git grep or findstr as fallback (rg might not be available on Windows)
$csFiles = Get-ChildItem -Path $SearchPath -Filter "*.cs" -Recurse -File

$referencingFiles = @()
foreach ($file in $csFiles) {
    if ($file.FullName -ne (Resolve-Path $Target -ErrorAction SilentlyContinue)) {
        $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
        if ($content -match $pattern) {
            $referencingFiles += $file.FullName
        }
    }
}

if ($referencingFiles) {
    $referencingFiles | ForEach-Object { Write-Host "  $_" }
}
else {
    Write-Host "(no references found)"
}

Write-Host ""
Write-Host "=== Usage contexts ===" -ForegroundColor Cyan

$count = 0
foreach ($file in $referencingFiles) {
    if ($count -ge 20) { 
        Write-Host "  ... (truncated, showing first 20 files)"
        break 
    }
    $lineNum = 0
    Get-Content $file | ForEach-Object {
        $lineNum++
        if ($_ -match $pattern) {
            Write-Host "${file}:${lineNum}: $_"
        }
    }
    $count++
}
