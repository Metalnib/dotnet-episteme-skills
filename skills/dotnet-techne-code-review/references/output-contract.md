# Output Contract
Use this structure exactly when presenting findings.

## Summary
- What changed / what you reviewed
- Review mode: Standard / Cynical
- Overall risk level: Low / Medium / High

## Findings
Each finding must include:
- **Severity:** blocking / important / suggestion
- **Area:** correctness | style | performance | AOT | security | DB | messaging | logging
- **Location:** file + line + type/method
- **Evidence:** short code snippet or command evidence
- **Impact:** production failure mode
- **Fix:** concrete recommendation (minimal patch guidance when possible)
- **Counter-check:** what could make this acceptable and why it is still an issue (or why severity is downgraded)
- **Confidence:** high / medium / low

## Quick wins (top 3)
- The three highest ROI changes that can be applied immediately.

## Follow-ups (optional)
- Larger refactors or architectural changes only when necessary.

## Cynical findings list (descriptions only)
- If user requests "descriptions only", output Markdown bullets with concise finding descriptions.
- Keep descriptions concrete and evidence-based.

## Halt conditions
- HALT and ask for clarification if there is no content to review.
- In cynical mode, if findings are zero, run one more adversarial pass from a different lens (correctness/perf/security/data/messaging).
- If findings are still zero, report "No confirmed issues after adversarial pass" and request deeper or narrower context.
- Never invent findings just to satisfy a count target.
