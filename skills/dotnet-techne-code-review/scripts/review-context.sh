#!/bin/bash
# Generate full review context for AI code review
# Combines: changed files, their content, and dependency info
# Usage: ./review-context.sh [target-branch|commit-range] [--brief|--full]

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET="${1:-main}"
MODE="${2:---brief}"
RANGE_SPEC=""

echo "# Code Review Context"
echo "Generated: $(date -Iseconds)"
echo "Target: $TARGET"
echo ""

# Get changed files
if [[ "$TARGET" == *".."* ]]; then
    RANGE_SPEC="$TARGET"
else
    MERGE_BASE=$(git --no-pager merge-base HEAD "$TARGET" 2>/dev/null || echo "$TARGET")
    RANGE_SPEC="$MERGE_BASE...HEAD"
fi

if ! DIFF_FILE_LIST=$(git --no-pager diff --name-only "$RANGE_SPEC" 2>/dev/null); then
    echo "Invalid review target: $TARGET"
    exit 1
fi

CHANGED_FILES=$(echo "$DIFF_FILE_LIST" | grep -E '\.cs$' || true)

if [ -z "$CHANGED_FILES" ]; then
    echo "No C# files changed."
    exit 0
fi

echo "## Changed C# Files ($(echo "$CHANGED_FILES" | wc -l | tr -d ' '))"
echo '```'
echo "$CHANGED_FILES"
echo '```'
echo ""

# Commits in range
echo "## Commits"
echo '```'
if [[ "$TARGET" == *".."* ]]; then
    git --no-pager log --oneline "$TARGET"
else
    git --no-pager log --oneline "$RANGE_SPEC" 2>/dev/null || git --no-pager log --oneline -10
fi
echo '```'
echo ""

# Changed .csproj files
CHANGED_PROJ=$(git --no-pager diff --name-only "$RANGE_SPEC" 2>/dev/null | grep -E '\.csproj$' || true)
if [ -n "$CHANGED_PROJ" ]; then
    echo "## Changed Project Files"
    echo '```'
    echo "$CHANGED_PROJ"
    echo '```'
    echo ""
fi

echo "## Potential Risk Signals (heuristic)"
echo '```'
git --no-pager diff "$RANGE_SPEC" -- "*.cs" 2>/dev/null | grep -nE 'TODO|FIXME|HACK|throw new NotImplementedException|catch[[:space:]]*\(Exception|\.Result\b|\.Wait\(|#pragma warning disable|AllowAnonymous|FromSqlRaw|ExecuteSqlRaw|IgnoreQueryFilters|Task\.Run\(' | head -80 || echo "(no heuristic risk signals matched)"
echo '```'
echo ""

if [ "$MODE" == "--full" ]; then
    echo "## Full Diff"
    echo '```diff'
    git --no-pager diff "$RANGE_SPEC" -- "*.cs" 2>/dev/null || true
    echo '```'
    echo ""
    
    echo "## File Contents (changed files)"
    echo "$CHANGED_FILES" | while read -r file; do
        if [ -f "$file" ]; then
            echo "### $file"
            echo '```csharp'
            cat "$file"
            echo '```'
            echo ""
        fi
    done
else
    echo "## Diff Stats"
    echo '```'
    git --no-pager diff --stat "$RANGE_SPEC" -- "*.cs" 2>/dev/null || true
    echo '```'
    echo ""
    
    echo "## Changed Methods/Classes (signatures)"
    echo "$CHANGED_FILES" | while read -r file; do
        if [ -f "$file" ]; then
            echo "### $file"
            echo '```'
            grep -nE '^\s*(public|private|protected|internal).*\s+(class|interface|record|struct|enum|void|async|Task|string|int|bool|var)\s+\w+' "$file" 2>/dev/null | head -30 || echo "(no public members found)"
            echo '```'
            echo ""
        fi
    done
fi

echo "---"
echo "Use --full for complete file contents and diff."
