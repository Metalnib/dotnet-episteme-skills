#!/bin/bash
# List all changed files with categorization for C# projects
# Usage: ./list-changes.sh [commit-range|branch]
# Examples:
#   ./list-changes.sh main          # Compare to main branch
#   ./list-changes.sh HEAD~3..HEAD  # Last 3 commits
#   ./list-changes.sh               # Uncommitted changes

set -e

RANGE="${1:-}"

get_files() {
    if [ -z "$RANGE" ]; then
        # Uncommitted changes (staged + unstaged + untracked)
        git --no-pager diff --name-only HEAD 2>/dev/null || git --no-pager diff --name-only
        git --no-pager diff --name-only --cached 2>/dev/null || true
        git --no-pager ls-files --others --exclude-standard 2>/dev/null || true
    elif [[ "$RANGE" == *".."* ]]; then
        # Explicit range
        git --no-pager diff --name-only "$RANGE"
    else
        # Branch comparison
        MERGE_BASE=$(git --no-pager merge-base HEAD "$RANGE" 2>/dev/null || echo "$RANGE")
        git --no-pager diff --name-only "$MERGE_BASE"...HEAD
    fi
}

FILES=$(get_files | sort -u)

echo "=== Changed Files Summary ==="
echo ""

# C# source files
CS_FILES=$(echo "$FILES" | grep -E '\.cs$' || true)
if [ -n "$CS_FILES" ]; then
    echo "## C# Source Files ($(echo "$CS_FILES" | wc -l | tr -d ' '))"
    echo "$CS_FILES" | sed 's/^/  - /'
    echo ""
fi

# Project files
PROJ_FILES=$(echo "$FILES" | grep -E '\.(csproj|sln|props|targets)$' || true)
if [ -n "$PROJ_FILES" ]; then
    echo "## Project/Build Files ($(echo "$PROJ_FILES" | wc -l | tr -d ' '))"
    echo "$PROJ_FILES" | sed 's/^/  - /'
    echo ""
fi

# Config files
CONFIG_FILES=$(echo "$FILES" | grep -E '\.(json|xml|yaml|yml|config)$' || true)
if [ -n "$CONFIG_FILES" ]; then
    echo "## Config Files ($(echo "$CONFIG_FILES" | wc -l | tr -d ' '))"
    echo "$CONFIG_FILES" | sed 's/^/  - /'
    echo ""
fi

# Migrations (EF Core)
MIGRATION_FILES=$(echo "$FILES" | grep -iE 'migration' || true)
if [ -n "$MIGRATION_FILES" ]; then
    echo "## Database Migrations ($(echo "$MIGRATION_FILES" | wc -l | tr -d ' '))"
    echo "$MIGRATION_FILES" | sed 's/^/  - /'
    echo ""
fi

# Test files
TEST_FILES=$(echo "$FILES" | grep -iE '(test|spec)' || true)
if [ -n "$TEST_FILES" ]; then
    echo "## Test Files ($(echo "$TEST_FILES" | wc -l | tr -d ' '))"
    echo "$TEST_FILES" | sed 's/^/  - /'
    echo ""
fi

# Other files
OTHER_FILES=$(echo "$FILES" | grep -vE '\.(cs|csproj|sln|props|targets|json|xml|yaml|yml|config)$' | grep -viE '(test|spec|migration)' || true)
if [ -n "$OTHER_FILES" ]; then
    echo "## Other Files ($(echo "$OTHER_FILES" | wc -l | tr -d ' '))"
    echo "$OTHER_FILES" | sed 's/^/  - /'
    echo ""
fi

echo "=== Total: $(echo "$FILES" | wc -l | tr -d ' ') files ==="
