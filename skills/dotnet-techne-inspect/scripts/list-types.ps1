# List public types (classes, interfaces, enums) in a NuGet package.
# Usage: list-types.ps1 -PackageName <name> -Version <ver> [-Tfm <tfm>]
# Auto-detects whether to use dotnet-inspect or ilspycmd.

param(
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
        $args = @('type', '--package', $PackageName, '--version', $Version)
        if ($Tfm) { $args += @('--framework', $Tfm) }
        & dotnet-inspect @args
    }
    'ilspycmd' {
        $findArgs = @{ PackageName = $PackageName; Version = $Version }
        if ($Tfm) { $findArgs.Tfm = $Tfm }
        $dll = & "$scriptDir\find-dll.ps1" @findArgs
        & ilspycmd -l ci $dll
    }
    default {
        Write-Error "Unknown tool: $tool"
        exit 1
    }
}
