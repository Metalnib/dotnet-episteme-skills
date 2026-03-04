#!/bin/bash
# Get all changes between current branch and target branch (default: main)
# Usage: ./branch-diff.sh [target-branch] [--stat|--files|--full]

set -e

TARGET_BRANCH="${1:-main}"
MODE="${2:---stat}"

# Find merge base
MERGE_BASE=$(git merge-base HEAD "$TARGET_BRANCH" 2>/dev/null || echo "$TARGET_BRANCH")

case "$MODE" in
    --stat)
        echo "=== Branch diff stats vs $TARGET_BRANCH ==="
        git diff --stat "$MERGE_BASE"...HEAD
        ;;
    --files)
        echo "=== Changed files vs $TARGET_BRANCH ==="
        git diff --name-only "$MERGE_BASE"...HEAD
        ;;
    --full)
        echo "=== Full diff vs $TARGET_BRANCH ==="
        git diff "$MERGE_BASE"...HEAD
        ;;
    --cs)
        echo "=== C# files changed vs $TARGET_BRANCH ==="
        git diff --name-only "$MERGE_BASE"...HEAD | grep -E '\.(cs|csproj|sln)$' || echo "(no C# files changed)"
        ;;
    *)
        echo "Usage: $0 [target-branch] [--stat|--files|--full|--cs]"
        exit 1
        ;;
esac
