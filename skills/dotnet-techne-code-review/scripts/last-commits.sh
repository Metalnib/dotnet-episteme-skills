#!/bin/bash
# Get changes from last N commits
# Usage: ./last-commits.sh [n] [--stat|--files|--full|--log]

set -e

N="${1:-1}"
MODE="${2:---stat}"

case "$MODE" in
    --stat)
        echo "=== Stats for last $N commit(s) ==="
        git --no-pager diff --stat HEAD~"$N"..HEAD
        ;;
    --files)
        echo "=== Files changed in last $N commit(s) ==="
        git --no-pager diff --name-only HEAD~"$N"..HEAD
        ;;
    --full)
        echo "=== Full diff for last $N commit(s) ==="
        git --no-pager diff HEAD~"$N"..HEAD
        ;;
    --log)
        echo "=== Log for last $N commit(s) ==="
        git --no-pager log --oneline -n "$N"
        echo ""
        echo "=== Detailed log ==="
        git --no-pager log --stat -n "$N"
        ;;
    --cs)
        echo "=== C# files changed in last $N commit(s) ==="
        git --no-pager diff --name-only HEAD~"$N"..HEAD | grep -E '\.(cs|csproj|sln)$' || echo "(no C# files changed)"
        ;;
    *)
        echo "Usage: $0 [n] [--stat|--files|--full|--log|--cs]"
        exit 1
        ;;
esac
