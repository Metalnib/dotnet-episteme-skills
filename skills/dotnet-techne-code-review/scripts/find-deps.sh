#!/bin/bash
# Find dependencies and usages for a C# type/file
# Usage: ./find-deps.sh <file-or-type> [search-path]

set -e

TARGET="${1:?Usage: $0 <file-or-type> [search-path]}"
SEARCH_PATH="${2:-.}"

# Extract type name from file path if given
if [[ "$TARGET" == *.cs ]]; then
    # Get class/interface/record names from the file
    if [ -f "$TARGET" ]; then
        TYPES=$(grep -oE '(class|interface|record|struct|enum)\s+\w+' "$TARGET" | awk '{print $2}' | tr '\n' '|' | sed 's/|$//')
        echo "=== Types defined in $TARGET ==="
        grep -oE '(class|interface|record|struct|enum)\s+\w+' "$TARGET" | sed 's/^/  /'
        echo ""
    else
        echo "File not found: $TARGET"
        exit 1
    fi
else
    TYPES="$TARGET"
fi

if [ -z "$TYPES" ]; then
    echo "No types found in $TARGET"
    exit 1
fi

echo "=== Files referencing: $TYPES ==="
if command -v rg >/dev/null 2>&1; then
    rg -l "($TYPES)" --type cs "$SEARCH_PATH" 2>/dev/null | grep -v "^$TARGET$" | sort -u || echo "(no references found)"
else
    grep -R -l -E --include='*.cs' "($TYPES)" "$SEARCH_PATH" 2>/dev/null | grep -v "^$TARGET$" | sort -u || echo "(no references found)"
fi
echo ""

echo "=== Usage contexts ==="
if command -v rg >/dev/null 2>&1; then
    rg -n "($TYPES)" --type cs "$SEARCH_PATH" -C 1 2>/dev/null | head -100 || echo "(no usages found)"
else
    grep -R -n -E --include='*.cs' "($TYPES)" "$SEARCH_PATH" 2>/dev/null | head -100 || echo "(no usages found)"
fi
