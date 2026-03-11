$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "../../..")
$srcDir = Join-Path $repoRoot "src/synopsis"

$rid = "win-x64"
if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') {
    $rid = "win-arm64"
}

# -Lean flag: framework-dependent (smaller, needs .NET 10 SDK on target)
$selfContained = "true"
if ($args -contains "--lean") {
    $selfContained = "false"
    Write-Host "Building framework-dependent (requires .NET 10 on target)..."
} else {
    Write-Host "Building self-contained for $rid (no SDK needed on target)..."
}

Push-Location $srcDir
try {
    dotnet publish Synopsis/Synopsis.csproj -c Release -r $rid `
        --self-contained $selfContained `
        -o "artifacts/$rid"
} finally {
    Pop-Location
}

$binary = Join-Path $srcDir "artifacts/$rid/synopsis.exe"
if (-not (Test-Path $binary)) {
    Write-Error "Build failed - binary not found at $binary"
    exit 1
}

$size = (Get-Item $binary).Length / 1MB
Write-Host ""
Write-Host "Published: $binary ($([math]::Round($size))MB)"
Write-Host ""
Write-Host "Add to PATH:"
Write-Host "  `$env:PATH += `";$srcDir\artifacts\$rid`""
