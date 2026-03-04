# Resolve the DLL path for a NuGet package from the local cache.
# Usage: find-dll.ps1 -PackageName <name> -Version <ver> [-Tfm <tfm>]
# If Tfm is omitted, picks the highest available TFM.

param(
    [Parameter(Mandatory=$true)]
    [string]$PackageName,

    [Parameter(Mandatory=$true)]
    [string]$Version,

    [string]$Tfm = ''
)

$ErrorActionPreference = 'Stop'

$packageLower = $PackageName.ToLowerInvariant()

# Determine NuGet cache path
$nugetCache = $env:NUGET_PACKAGES
if (-not $nugetCache) {
    if ($env:USERPROFILE) {
        $nugetCache = Join-Path $env:USERPROFILE '.nuget' 'packages'
    } else {
        $nugetCache = Join-Path $HOME '.nuget' 'packages'
    }
}

$packageDir = Join-Path $nugetCache $packageLower $Version
if (-not (Test-Path $packageDir)) {
    Write-Error "Package not found in NuGet cache: $packageDir`nRun 'dotnet restore' in a project that references $PackageName $Version"
    exit 1
}

$libDir = Join-Path $packageDir 'lib'
if (-not (Test-Path $libDir)) {
    Write-Error "No lib/ directory found in $packageDir"
    exit 1
}

# Resolve TFM
if (-not $Tfm) {
    $Tfm = Get-ChildItem -Path $libDir -Directory |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty Name
    if (-not $Tfm) {
        Write-Error "No TFM directories found in $libDir"
        exit 1
    }
}

$tfmDir = Join-Path $libDir $Tfm
if (-not (Test-Path $tfmDir)) {
    $available = (Get-ChildItem -Path $libDir -Directory).Name -join ', '
    Write-Error "TFM '$Tfm' not found. Available: $available"
    exit 1
}

$dll = Get-ChildItem -Path $tfmDir -Filter '*.dll' | Select-Object -First 1
if (-not $dll) {
    Write-Error "No DLL found in $tfmDir"
    exit 1
}

Write-Output $dll.FullName
