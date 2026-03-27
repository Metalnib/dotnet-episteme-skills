#!/bin/bash
# Find test files that may be affected by changes
# Usage: ./affected-tests.sh [target-branch|commit-range]

set -e

TARGET="${1:-main}"

# Get changed files
if [[ "$TARGET" == *".."* ]]; then
    CHANGED_FILES=$(git --no-pager diff --name-only "$TARGET" | grep -E '\.cs$' || true)
else
    MERGE_BASE=$(git --no-pager merge-base HEAD "$TARGET" 2>/dev/null || echo "$TARGET")
    CHANGED_FILES=$(git --no-pager diff --name-only "$MERGE_BASE"...HEAD | grep -E '\.cs$' || true)
fi

if [ -z "$CHANGED_FILES" ]; then
    echo "No C# files changed."
    exit 0
fi

echo "=== Changed Source Files ==="
# Filter out test files from changed files
SOURCE_FILES=$(echo "$CHANGED_FILES" | grep -viE '(test|spec)' || true)
echo "$SOURCE_FILES" | sed 's/^/  /'
echo ""

echo "=== Changed Test Files ==="
TEST_FILES=$(echo "$CHANGED_FILES" | grep -iE '(test|spec)' || true)
if [ -n "$TEST_FILES" ]; then
    echo "$TEST_FILES" | sed 's/^/  /'
else
    echo "  (none)"
fi
echo ""

echo "=== Potentially Affected Tests ==="
# For each changed source file, find corresponding test files
echo "$SOURCE_FILES" | while read -r file; do
    if [ -z "$file" ]; then continue; fi
    
    # Get the base name without path and extension
    BASENAME=$(basename "$file" .cs)
    
    # Search for test files matching this name
    MATCHING_TESTS=$(find . -type f \( -name "*${BASENAME}*Test*.cs" -o -name "*${BASENAME}*Spec*.cs" -o -name "*Test*${BASENAME}*.cs" \) 2>/dev/null | head -10 || true)
    
    if [ -n "$MATCHING_TESTS" ]; then
        echo "  $file:"
        echo "$MATCHING_TESTS" | sed 's/^/    -> /'
    fi
done

echo ""
echo "=== Test Projects ==="
find . -name "*.csproj" -type f -exec grep -l "Microsoft.NET.Test.Sdk\|xunit\|NUnit\|MSTest" {} \; 2>/dev/null | sed 's/^/  /' || echo "  (none found)"
