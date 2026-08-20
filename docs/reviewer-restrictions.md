# Worker restrictions — the contract

The plugin's worker agents are advisory: they read, they never write, and they
must not leak what they read. Three lane groups carry the contract — the six
review lanes, the four refactor lanes (`cartographer`, `tracer`, `surveyor`,
`conformance-auditor`), and the three qa lanes (`acceptance`, `reuse-design`,
`dead-code`). One posture, enforced three ways — **change one implementation,
change all three** (they have drifted before).

## The posture

| # | Rule | Why |
|---|---|---|
| R1 | No file writes or edits | workers advise; orchestrators change things |
| R2 | Shell limited to read-only git (`diff`, `log`, `show`, `blame`, `status`, `rev-parse`, `merge-base`; `git diff --no-index` is denied — it reads arbitrary files outside the repo) and read-only synopsis (`query`, `git-scan`, `scan`, `diff`, `breaking-diff`). Every lane also gets the read-only search/list tools — rg/fd (probed via `command -v`, GNU grep/find as fallbacks), ls, eza, cat, head, tail, wc, tree, stat, file — with each tool's own write/exec flags denied (find/fd exec/delete, `rg --pre`/`--hostname-bin`/`--search-zip`/`-z`, `tree -o`, `file -C`), so no allowed tool can run a subprocess or write a file even with no shell operator. Paths must resolve inside the project directory, absolute ones included, and parent-directory escapes are refused. Review and qa lanes were denied these tools until 1.8.1; the only effect was a run of failed calls before they fell back to the Grep tool, since the native Read/Grep/Glob tools bypass this hook entirely | evidence-gathering only |
| R3 | No shell operators (`; \| & > < ` $( `). No `-o`/`--output` on git/synopsis; refactor search tools keep safe `-o`/`-x` (rg/grep only-matching, rg line-regexp) while each tool's write/exec flags are denied | an allowed prefix must not smuggle a write or a second command |
| R4 | `git -C` only inside the reviewed project | no reading other repos' history |
| R5 | No outbound network (fetch, search) | workers process untrusted diff text — no exfil channel |
| R6 | No spawning further agents | a nested agent would not inherit these restrictions |

## Per-tool implementation

| Tool | Mechanism | Covers |
|---|---|---|
| Claude Code | `hooks/git-readonly-guard.sh` (PreToolUse, scoped to all three lane groups) + `tools:` allow-list in `agents/*/*.md` (lanes that need Synopsis MCP tools — data-messaging, the refactor lanes, the qa lanes — instead deny-list Write/Edit/NotebookEdit/Task/WebFetch/WebSearch via `disallowedTools`, keeping the MCP tools visible) | R1–R6 |
| OpenCode | `REVIEWER_PERMISSION` in `opencode/dotnet-episteme.js` (refactor lanes get `REFACTOR_PERMISSION`, which adds the search-tool allows) — permission resolution is last-match-wins, so operator/output deny rules sit after the allows | R1–R6 |
| Codex | `sandbox_mode = "read-only"` per role (`scripts/install-codex.sh`, all thirteen roles) blocks writes and shell network; `hooks/git-readonly-guard.sh` scopes to the `review-*`/`refactor-*`/`qa-*` roles via the payload's `agent_type` for R2–R4 | R1–R4; R5 for shell (host-side web tools are governed by the user's Codex config); R6 n/a (roles cannot spawn) |

Regression nets: `scripts/test-guard.sh` (Claude + Codex payload shapes, all
three lane groups) and `scripts/test-opencode-plugin.mjs` (OpenCode permission
map). `scripts/validate.sh` checks each lane's frontmatter, whichever style it
uses.
