# Reviewer restrictions — the contract

Review agents are advisory: they read, they never write, and they must not leak
what they read. One posture, enforced three ways — **change one implementation,
change all three** (they have drifted before).

## The posture

| # | Rule | Why |
|---|---|---|
| R1 | No file writes or edits | reviews change nothing |
| R2 | Shell limited to read-only git (`diff`, `log`, `show`, `blame`, `status`) and read-only synopsis (`query`, `git-scan`, `scan`, `diff`, `breaking-diff`) | evidence-gathering only |
| R3 | No shell operators (`; \| & > < ` $( `) and no `-o`/`--output` | an allowed prefix must not smuggle a write or a second command |
| R4 | `git -C` only inside the reviewed project | no reading other repos' history |
| R5 | No outbound network (fetch, search) | reviewers process untrusted diff text — no exfil channel |
| R6 | No spawning further agents | a nested agent would not inherit these restrictions |

## Per-tool implementation

| Tool | Mechanism | Covers |
|---|---|---|
| Claude Code | `hooks/git-readonly-guard.sh` (PreToolUse) + `tools:` allow-list in `agents/review/*.md` (data-messaging instead deny-lists Write/Edit/NotebookEdit/Task/WebFetch/WebSearch via `disallowedTools`, keeping the Synopsis MCP tools visible) | R1–R6 |
| OpenCode | `REVIEWER_PERMISSION` in `opencode/dotnet-episteme.js` — permission resolution is last-match-wins, so operator/output deny rules sit after the git allows | R1–R6 |
| Codex | `sandbox_mode = "read-only"` per role (`scripts/install-codex.sh`) blocks writes and shell network; `hooks/git-readonly-guard.sh` scopes to the `review-*` roles via the payload's `agent_type` for R2–R4 | R1–R4; R5 for shell (host-side web tools are governed by the user's Codex config); R6 n/a (roles cannot spawn) |

Regression nets: `scripts/test-guard.sh` (Claude + Codex payload shapes) and
`scripts/test-opencode-plugin.mjs` (OpenCode permission map).
