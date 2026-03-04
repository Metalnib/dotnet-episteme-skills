# Analyze .csproj dependencies and project references
# Usage: .\project-deps.ps1 [-Target <csproj-file|directory>]

param(
    [string]$Target = "."
)

$ErrorActionPreference = "Stop"

function Get-CsprojFiles {
    if ($Target -match '\.csproj$') {
        return @(Get-Item $Target)
    }
    else {
        return Get-ChildItem -Path $Target -Filter "*.csproj" -Recurse -File
    }
}

function Analyze-Csproj {
    param([System.IO.FileInfo]$Csproj)
    
    Write-Host "=== $($Csproj.FullName) ===" -ForegroundColor Cyan
    Write-Host ""
    
    [xml]$xml = Get-Content $Csproj.FullName
    
    # Project references
    $projRefs = $xml.Project.ItemGroup.ProjectReference | Where-Object { $_.Include }
    if ($projRefs) {
        Write-Host "## Project References:" -ForegroundColor Green
        $projRefs | ForEach-Object { Write-Host "  $($_.Include)" }
        Write-Host ""
    }
    
    # Package references
    $pkgRefs = $xml.Project.ItemGroup.PackageReference | Where-Object { $_.Include }
    if ($pkgRefs) {
        Write-Host "## NuGet Packages:" -ForegroundColor Green
        $pkgRefs | ForEach-Object { 
            $version = if ($_.Version) { " v$($_.Version)" } else { "" }
            Write-Host "  $($_.Include)$version"
        }
        Write-Host ""
    }
    
    # Target framework
    $tfm = $xml.Project.PropertyGroup.TargetFramework | Select-Object -First 1
    $tfms = $xml.Project.PropertyGroup.TargetFrameworks | Select-Object -First 1
    if ($tfm -or $tfms) {
        Write-Host "## Target Framework:" -ForegroundColor Green
        if ($tfm) { Write-Host "  $tfm" }
        if ($tfms) { Write-Host "  $tfms" }
        Write-Host ""
    }
    
    # Key properties
    Write-Host "## Key Properties:" -ForegroundColor Green
    $props = @("Nullable", "ImplicitUsings", "EnableAOT", "PublishAot", "IsTrimmable", "InvariantGlobalization")
    $found = $false
    foreach ($prop in $props) {
        $value = $xml.Project.PropertyGroup.$prop | Select-Object -First 1
        if ($value) {
            Write-Host "  $prop = $value"
            $found = $true
        }
    }
    if (-not $found) { Write-Host "  (none found)" }
    Write-Host ""
}

$csprojFiles = Get-CsprojFiles

if ($csprojFiles.Count -eq 0) {
    Write-Host "No .csproj files found in $Target" -ForegroundColor Red
    exit 1
}

foreach ($csproj in $csprojFiles) {
    Analyze-Csproj $csproj
}

# Build dependency graph
Write-Host "=== Project Dependency Graph ===" -ForegroundColor Cyan

foreach ($csproj in $csprojFiles) {
    $name = $csproj.BaseName
    [xml]$xml = Get-Content $csproj.FullName
    $refs = $xml.Project.ItemGroup.ProjectReference | Where-Object { $_.Include } | ForEach-Object {
        [System.IO.Path]::GetFileNameWithoutExtension($_.Include)
    }
    
    if ($refs) {
        Write-Host "  $name -> $($refs -join ', ')"
    }
    else {
        Write-Host "  $name (no project refs)"
    }
}
