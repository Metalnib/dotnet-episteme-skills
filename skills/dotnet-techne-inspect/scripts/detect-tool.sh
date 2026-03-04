#!/usr/bin/env bash
# Detect which .NET assembly inspection tool is available.
# Output: "dotnet-inspect" or "ilspycmd"
# Exit 1 if neither is found.

set -euo pipefail

if command -v dotnet-inspect &>/dev/null; then
    echo "dotnet-inspect"
elif command -v ilspycmd &>/dev/null; then
    echo "ilspycmd"
else
    echo "ERROR: Neither dotnet-inspect nor ilspycmd found on PATH." >&2
    echo "" >&2
    echo "Install one of these .NET global tools:" >&2
    echo "  dotnet tool install -g dotnet-inspect   # requires .NET 10+ SDK" >&2
    echo "  dotnet tool install -g ilspycmd          # requires .NET 8+ SDK" >&2
    exit 1
fi
