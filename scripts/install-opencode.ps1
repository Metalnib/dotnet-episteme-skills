#Requires -Version 5.1
<#
.SYNOPSIS
    Installs this repo's OpenCode plugin on Windows.
.DESCRIPTION
    Links opencode/dotnet-episteme.js into OpenCode's global plugin directory so
    the skills, the thirteen worker subagents, and the /dotnet-review, /dotnet-qa
    and /dotnet-refactor commands are registered.

    The Synopsis MCP server is NOT registered on native Windows: its launcher is
    a POSIX shell script. Run OpenCode under WSL2 (and use install-opencode.sh)
    for MCP, or drive Synopsis through the CLI with
    skills/dotnet-techne-synopsis/scripts/detect-tool.ps1.
.PARAMETER Verify
    Verify an existing install instead of creating the link.
.PARAMETER Uninstall
    Remove the link.
#>
[CmdletBinding()]
param(
    [switch]$Verify,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginSrc = Join-Path $repoRoot 'opencode\dotnet-episteme.js'
$configDir = Join-Path $env:USERPROFILE '.config\opencode'
$pluginDir = Join-Path $configDir 'plugin'
$link = Join-Path $pluginDir 'dotnet-episteme.js'

if ($Uninstall) {
    if (Test-Path $link) {
        Remove-Item $link -Force
        Write-Host "Removed $link"
    }
    else {
        Write-Host "Nothing to remove at $link"
    }
    exit 0
}

if (-not $Verify) {
    if (-not (Test-Path $pluginSrc)) { throw "Plugin not found: $pluginSrc" }
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

    # A symlink needs Developer Mode or elevation; fall back to a shim that
    # re-exports from the repo. A plain copy would resolve the plugin's ROOT to
    # the config dir and register nothing.
    try {
        New-Item -ItemType SymbolicLink -Path $link -Target $pluginSrc -Force | Out-Null
        Write-Host "Linked $link -> $pluginSrc"
    }
    catch {
        $srcUrl = 'file:///' + ($pluginSrc -replace '\\', '/')
        Set-Content -Path $link -Value "export { DotnetEpisteme } from `"$srcUrl`"" -Encoding utf8
        Write-Host "Symlink not permitted; wrote a shim instead (git pull updates still apply): $link"
    }

    # A hand-copied Claude agent file breaks OpenCode's whole config document.
    foreach ($dir in @((Join-Path $configDir 'agent'), (Join-Path $configDir 'agents'))) {
        if (Test-Path $dir) {
            $bad = Get-ChildItem $dir -Filter *.md -ErrorAction SilentlyContinue |
                Where-Object { (Get-Content $_.FullName -TotalCount 10) -match '^tools: *[A-Za-z]+,' }
            if ($bad) {
                Write-Warning "$dir contains agent files with a comma-string 'tools:' key: $($bad.Name -join ', ')"
                Write-Warning "OpenCode rejects the entire config over it. Delete them - the plugin registers the reviewers."
            }
        }
    }
}

if (-not (Get-Command opencode -ErrorAction SilentlyContinue)) {
    Write-Host 'opencode not on PATH - skipping verification.'
    Write-Host 'After starting OpenCode: opencode agent list | opencode debug config'
    exit 0
}

Write-Host ''
Write-Host 'Verifying with the OpenCode CLI:'
$config = & opencode debug config | ConvertFrom-Json
$failures = 0

function Test-Item($label, $ok, $detail = '') {
    if ($ok) { Write-Host "  OK    $label" }
    else {
        Write-Host "  FAIL  $label$(if ($detail) { " - $detail" })"
        $script:failures++
    }
}

$skillsPath = Join-Path $repoRoot 'skills'
Test-Item 'skills path registered' ($config.skills.paths -contains $skillsPath) "skills.paths = $($config.skills.paths -join ', ')"

$lanes = @(
    'review-correctness', 'review-performance', 'review-security-observability',
    'review-data-messaging', 'review-generalist', 'review-maintainer',
    'refactor-cartographer', 'refactor-tracer', 'refactor-conformance-auditor', 'refactor-surveyor',
    'qa-acceptance', 'qa-reuse-design', 'qa-dead-code'
)
$missing = $lanes | Where-Object { -not $config.agent.$_ }
Test-Item 'thirteen worker subagents registered' (-not $missing) "missing $($missing -join ', ')"
foreach ($name in @('dotnet-review', 'dotnet-qa', 'dotnet-refactor')) {
    Test-Item "/$name command registered" ($null -ne $config.command.$name)
}
Write-Host '  SKIP  Synopsis MCP (native Windows - use WSL2 or the CLI)'

Write-Host ''
if ($failures) { throw "$failures check(s) failed" }
Write-Host 'Done. Try /dotnet-review, /dotnet-qa or /dotnet-refactor in OpenCode.'
