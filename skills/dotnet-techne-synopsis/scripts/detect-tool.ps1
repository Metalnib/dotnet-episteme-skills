$ErrorActionPreference = 'Stop'

$Repo = "Metalnib/dotnet-episteme-skills"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$skillDir = Resolve-Path (Join-Path $scriptDir "..")
$binDir = Join-Path $skillDir "bin"

# Detect platform
$rid = "win-x64"
if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') {
    $rid = "win-arm64"
}

$binary = Join-Path $binDir "$rid/synopsis.exe"

# 1. Check PATH
$inPath = Get-Command synopsis -ErrorAction SilentlyContinue
if ($inPath) {
    Write-Output "synopsis"
    exit 0
}

# 2. Check skill bin/
if (Test-Path $binary) {
    Write-Output $binary
    exit 0
}

# 3. Check dev build artifacts
$repoRoot = Resolve-Path (Join-Path $skillDir "../..")
$candidates = @(
    Join-Path $repoRoot "src/synopsis/artifacts/$rid/synopsis.exe"
    Join-Path $repoRoot "src/synopsis/artifacts/win-x64/synopsis.exe"
)
foreach ($candidate in $candidates) {
    if (Test-Path $candidate) {
        Write-Output $candidate
        exit 0
    }
}

# 4. Auto-download from GitHub Releases
$asset = "synopsis-${rid}.zip"
$url = "https://github.com/${Repo}/releases/latest/download/${asset}"

Write-Host "Synopsis not found locally. Downloading for $rid..." -ForegroundColor Yellow

$targetDir = Join-Path $binDir $rid
New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

$zipPath = Join-Path $targetDir $asset
try {
    # Use .NET HttpClient directly - no external deps, handles redirects
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(5)

    $response = $client.GetAsync($url).Result
    if (-not $response.IsSuccessStatusCode) {
        Write-Error "Download failed (HTTP $($response.StatusCode)) from $url`nNo release found. Build from source: cd $repoRoot\src\synopsis && dotnet publish Synopsis/Synopsis.csproj -c Release -r $rid"
        exit 1
    }

    $bytes = $response.Content.ReadAsByteArrayAsync().Result
    [System.IO.File]::WriteAllBytes($zipPath, $bytes)
    $client.Dispose()
} catch {
    Remove-Item $zipPath -ErrorAction SilentlyContinue
    Write-Error "Download failed: $_`nBuild from source: cd $repoRoot\src\synopsis && dotnet publish Synopsis/Synopsis.csproj -c Release -r $rid"
    exit 1
}

# Extract - Expand-Archive is built into PowerShell 5+
Expand-Archive -Path $zipPath -DestinationPath $targetDir -Force
Remove-Item $zipPath -ErrorAction SilentlyContinue

if (Test-Path $binary) {
    Write-Host "Downloaded synopsis to $binary" -ForegroundColor Green
    Write-Output $binary
    exit 0
}

Write-Error "Download succeeded but binary not found at $binary"
exit 1
