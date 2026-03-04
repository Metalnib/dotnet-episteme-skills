#!/bin/bash
# Analyze .csproj dependencies and project references
# Usage: ./project-deps.sh [csproj-file|directory]

set -e

TARGET="${1:-.}"

find_csproj_files() {
    if [[ "$TARGET" == *.csproj ]]; then
        echo "$TARGET"
    else
        find "$TARGET" -name "*.csproj" -type f 2>/dev/null
    fi
}

analyze_csproj() {
    local CSPROJ="$1"
    echo "=== $CSPROJ ==="
    echo ""
    
    # Project references
    PROJ_REFS=$(grep -oE 'ProjectReference Include="[^"]+' "$CSPROJ" 2>/dev/null | sed 's/ProjectReference Include="/  /' || true)
    if [ -n "$PROJ_REFS" ]; then
        echo "## Project References:"
        echo "$PROJ_REFS"
        echo ""
    fi
    
    # Package references
    PKG_REFS=$(grep -oE 'PackageReference Include="[^"]+"[^/]*(Version="[^"]+")?' "$CSPROJ" 2>/dev/null | sed 's/PackageReference Include="/  /' | sed 's/"/ /' || true)
    if [ -n "$PKG_REFS" ]; then
        echo "## NuGet Packages:"
        echo "$PKG_REFS"
        echo ""
    fi
    
    # Target framework
    TFM=$(grep -oE '<TargetFramework[s]?>[^<]+' "$CSPROJ" 2>/dev/null | sed 's/<TargetFramework[s]?>/  /' | head -1 || true)
    if [ -n "$TFM" ]; then
        echo "## Target Framework:"
        echo "$TFM"
        echo ""
    fi
    
    # Important properties
    echo "## Key Properties:"
    grep -E '<(Nullable|ImplicitUsings|EnableAOT|PublishAot|IsTrimmable|InvariantGlobalization)>' "$CSPROJ" 2>/dev/null | sed 's/^/  /' || echo "  (none found)"
    echo ""
}

CSPROJ_FILES=$(find_csproj_files)

if [ -z "$CSPROJ_FILES" ]; then
    echo "No .csproj files found in $TARGET"
    exit 1
fi

echo "$CSPROJ_FILES" | while read -r csproj; do
    analyze_csproj "$csproj"
done

# Build dependency graph
echo "=== Project Dependency Graph ==="
echo "$CSPROJ_FILES" | while read -r csproj; do
    NAME=$(basename "$csproj" .csproj)
    REFS=$(grep -oE 'ProjectReference Include="[^"]+' "$csproj" 2>/dev/null | sed 's/.*[\\\/]//' | sed 's/\.csproj//' | tr '\n' ', ' | sed 's/,$//' || true)
    if [ -n "$REFS" ]; then
        echo "  $NAME -> $REFS"
    else
        echo "  $NAME (no project refs)"
    fi
done
