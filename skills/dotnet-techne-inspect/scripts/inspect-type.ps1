# Decompile a specific type from a NuGet package to show its full API surface.
# Usage: inspect-type.ps1 -TypeName <full-type-name> -PackageName <name> -Version <ver> [-Tfm <tfm>]
# Auto-detects whether to use dotnet-inspect or ilspycmd.

param(
    [Parameter(Mandatory=$true)]
    [string]$TypeName,

    [Parameter(Mandatory=$true)]
    [string]$PackageName,

    [Parameter(Mandatory=$true)]
    [string]$Version,

    [string]$Tfm = ''
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$tool = & "$scriptDir\detect-tool.ps1"

switch ($tool) {
    'dotnet-inspect' {
        $args = @('member', '--type', $TypeName, '--package', $PackageName, '--version', $Version)
        if ($Tfm) { $args += @('--framework', $Tfm) }
        & dotnet-inspect @args
    }
    'ilspycmd' {
        $findArgs = @{ PackageName = $PackageName; Version = $Version }
        if ($Tfm) { $findArgs.Tfm = $Tfm }
        $dll = & "$scriptDir\find-dll.ps1" @findArgs
        & ilspycmd -t $TypeName $dll
    }
    default {
        Write-Error "Unknown tool: $tool"
        exit 1
    }
}
