# Installs the dotnet-review dynamic workflow as a native /dotnet-review command
# by copying it into a .claude/workflows/ directory. Re-run after plugin updates.
param(
    [ValidateSet('user', 'project')]
    [string]$Scope = 'user'
)
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $scriptDir '..\workflows\dotnet-review.js'
if (-not (Test-Path $source)) {
    Write-Error "Source not found: $source"
    exit 1
}

$configDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
$targetDir = if ($Scope -eq 'project') { Join-Path (Get-Location) '.claude\workflows' } else { Join-Path $configDir 'workflows' }

New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
Copy-Item $source (Join-Path $targetDir 'dotnet-review.js') -Force

$version = (Select-String -Path $source -Pattern '^// version: *(.+)$' | Select-Object -First 1).Matches.Groups[1].Value
Write-Host "Installed dotnet-review workflow $version to $targetDir\dotnet-review.js"
Write-Host "Run it as /dotnet-review in Claude Code (requires the dotnet-episteme-skills plugin for the reviewer agents)."
Write-Host "Re-run this script after plugin updates to pick up workflow changes."
