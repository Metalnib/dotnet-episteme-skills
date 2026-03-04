# Detect which .NET assembly inspection tool is available.
# Output: "dotnet-inspect" or "ilspycmd"
# Exit 1 if neither is found.

$ErrorActionPreference = 'Stop'

if (Get-Command 'dotnet-inspect' -ErrorAction SilentlyContinue) {
    Write-Output 'dotnet-inspect'
}
elseif (Get-Command 'ilspycmd' -ErrorAction SilentlyContinue) {
    Write-Output 'ilspycmd'
}
else {
    Write-Error @"
Neither dotnet-inspect nor ilspycmd found on PATH.

Install one of these .NET global tools:
  dotnet tool install -g dotnet-inspect   # requires .NET 10+ SDK
  dotnet tool install -g ilspycmd          # requires .NET 8+ SDK
"@
    exit 1
}
