export const meta = {
  // -workers keeps pickers from showing a twin of the command, which launches this by scriptPath, not name.
  name: 'dotnet-qa-workers',
  description: 'Not a command - this is the engine /dotnet-qa starts under the hood; type /dotnet-qa to run a story QA. It runs the scout, the 3 QA lanes in parallel (acceptance vs spec, reuse/design, dead code), the maintainer falsification, and computes the gate.',
  whenToUse: 'Invoked by the /dotnet-qa command after it resolved the spec - not standalone (spec discovery needs the user). Pass args {target, base?, specPack?, practicesPack?, model?, pluginRoot?}.',
  phases: [
    { title: 'Scope', detail: 'scout captures the story diff vs the merge-base' },
    { title: 'Audit', detail: '3 QA lanes in parallel: acceptance (spec mode), reuse/design, dead code' },
    { title: 'Verify', detail: 'maintainer falsification, then the deterministic gate' },
  ],
}

// version: 1.8.0 (keep in sync with plugin.json; installer prints this line)
const input = typeof args === 'string' ? JSON.parse(args) : (args ?? {})
const target = input.target ?? 'current branch'
const noSpec = !input.specPack

// ─── Schemas ───
const SCOPE_SCHEMA = {
  type: 'object', required: ['repoRoot', 'base', 'files', 'locChanged', 'summary', 'diff'],
  properties: {
    repoRoot: { type: 'string' },
    base: { type: 'string' },
    files: { type: 'array', maxItems: 300, items: { type: 'string' } },
    locChanged: { type: 'integer' },
    summary: { type: 'string' },
    diff: { type: 'string' },
  },
}
const FINDING_PROPS = {
  severity: { enum: ['blocking', 'important', 'suggestion'] },
  area: { enum: ['acceptance', 'reuse', 'design', 'dead-code', 'stale-words', 'comments'] },
  owner: { enum: ['code', 'backlog', 'test-suite', 'docs'] },
  location: { type: 'string' },
  evidence: { type: 'string' },
  impact: { type: 'string' },
  fix: { type: 'string' },
  confidence: { enum: ['high', 'medium', 'low'] },
}
const FINDINGS_SCHEMA = {
  type: 'object', required: ['findings'],
  properties: {
    findings: {
      type: 'array', maxItems: 30,
      items: { type: 'object', required: Object.keys(FINDING_PROPS), properties: FINDING_PROPS },
    },
  },
}
const ACCEPTANCE_SCHEMA = {
  type: 'object', required: ['acCoverage', 'findings'],
  properties: {
    acCoverage: {
      type: 'array', maxItems: 40,
      items: {
        type: 'object', required: ['ac', 'criterion', 'verdict', 'evidence', 'provingTest'],
        properties: {
          ac: { type: 'string' },
          criterion: { type: 'string' },
          verdict: { enum: ['IMPLEMENTED', 'PARTIAL', 'MISSING'] },
          evidence: { type: 'string' },
          provingTest: { type: 'string', description: 'test name, or exactly NONE' },
        },
      },
    },
    findings: FINDINGS_SCHEMA.properties.findings,
  },
}
const VERDICTS_SCHEMA = {
  type: 'object', required: ['verdicts'],
  properties: {
    verdicts: {
      type: 'array',
      items: {
        type: 'object', required: ['index', 'verdict', 'rationale'],
        properties: {
          index: { type: 'integer' },
          verdict: { enum: ['CONFIRMED', 'UPGRADED', 'DOWNGRADED', 'REFUTED'] },
          newSeverity: { enum: ['blocking', 'important', 'suggestion'] },
          rationale: { type: 'string' },
        },
      },
    },
    acDisputes: {
      type: 'array', maxItems: 20,
      description: 'AC verdicts whose cited evidence did not hold - advisory challenges, the table itself is never rewritten',
      items: {
        type: 'object', required: ['ac', 'rationale'],
        properties: { ac: { type: 'string' }, rationale: { type: 'string' } },
      },
    },
  },
}

// ─── Paths ───
const reviewRefs = input.pluginRoot
  ? `${input.pluginRoot}/skills/dotnet-techne-code-review/references`
  : 'the references directory of the dotnet-episteme-skills plugin: Glob for ~/.claude/plugins/cache/*/dotnet-episteme-skills/*/skills/dotnet-techne-code-review/references; if nothing matches, proceed from the playbook knowledge in your agent definition'
const commentRules = input.pluginRoot
  ? `${input.pluginRoot}/skills/dotnet-techne-story-qa/references/comment-rules.md`
  : 'the comment-rules.md under the plugin\'s dotnet-techne-story-qa/references (Glob for ~/.claude/plugins/cache/*/dotnet-episteme-skills/*/skills/dotnet-techne-story-qa/references/comment-rules.md); if nothing matches, apply why-only one-line comment discipline from your agent definition'
const qaFalsification = input.pluginRoot
  ? `${input.pluginRoot}/skills/dotnet-techne-story-qa/references/falsification.md`
  : 'the falsification.md under the plugin\'s dotnet-techne-story-qa/references; if not found, apply the maintainer playbook alone'

// ─── Phase: Scope ───
phase('Scope')
const scope = await agent(
  `You are scoping a story QA run. Target: ${target}. Work read-only (git, file reads).
Determine and return per the schema:
- repoRoot (absolute path).
- base: ${input.base ? `use ${input.base}` : 'the merge-base of the target with the default branch (git merge-base HEAD origin/HEAD or main/master)'}.
- files: every file the story changed (base...target), and locChanged (git diff --shortstat additions+deletions). If more than 300 files changed, list the 300 most story-relevant and state the true total in the summary - never truncate silently.
- summary: what the story changes, max 10 lines.
- diff: the unified diff base...target. Up to ~1500 lines include it verbatim; above that, include full hunks for non-test source files and per-file summaries (path + what changed) for tests and generated code. NEVER include the content of likely-secret files (.env*, *.pem, *.key, credentials*, secrets*, or config files containing connection strings or tokens) - list them by name and note the exclusion in the summary.
If there is nothing to review, return an empty files array and an empty diff.`,
  { label: 'scout', effort: 'low', schema: SCOPE_SCHEMA },
)
if (!scope || scope.files.length === 0) {
  return { halted: true, reason: `Nothing to QA for target: ${target}` }
}

// No runtime model sizing: every lane inherits the session model - the
// session tier is enough for these validations, and auto-escalating to
// fable doubles the lane cost. --model stays as the explicit override.
const tier = input.model ?? 'inherit'
const modelOpt = tier === 'inherit' ? {} : { model: tier }
if (input.model) log(`Model tier: ${tier} (--model override)`)

// ─── Phase: Audit ───
phase('Audit')
const common = `Repo root: ${scope.repoRoot}. Story target: ${target} (base: ${scope.base}).
Story summary: ${scope.summary}
The diff below is your primary material. Explore further whenever you judge it necessary - Read/Grep/Glob, plus read-only git via Bash (a plugin hook blocks everything else). Every finding needs file:line evidence. Report nothing outside your lane.
Changed files:\n${scope.files.join('\n')}
Diff:\n${scope.diff}`

const LANES = [
  ...(noSpec ? [] : [{
    name: 'acceptance',
    schema: ACCEPTANCE_SCHEMA,
    prompt: `Spec pack (the story's contract - verify the implementation against it):\n${input.specPack}\n${common}`,
  }]),
  {
    name: 'reuse-design',
    schema: FINDINGS_SCHEMA,
    prompt: `${input.practicesPack ? `Practices pack (the project's established conventions):\n${input.practicesPack}\n` : ''}${common}
For graph evidence use Synopsis per your agent definition: load the MCP tools via ToolSearch first (they are deferred), or fall back to the read-only synopsis CLI.`,
  },
  {
    name: 'dead-code',
    schema: FINDINGS_SCHEMA,
    prompt: `Comment rules: ${commentRules} (host-repo conventions in CLAUDE.md or style docs override them - say which you applied).\n${common}`,
  },
]
if (noSpec) log('No-spec mode (no spec pack passed): acceptance lane skipped')

const rawResults = await parallel(LANES.map(l => () =>
  agent(l.prompt, { agentType: `dotnet-episteme-skills:qa:${l.name}`, label: `qa:${l.name}`, phase: 'Audit', schema: l.schema, ...modelOpt })))

const lanesFailed = LANES.filter((l, i) => !rawResults[i]).map(l => l.name)
if (lanesFailed.length === LANES.length) {
  return { halted: true, reason: `All QA lanes failed to launch (${lanesFailed.join(', ')}). Is the dotnet-episteme-skills plugin installed and enabled?` }
}
if (lanesFailed.length > 0) log(`WARNING: lanes failed, the QA is partial: ${lanesFailed.join(', ')}`)

const acceptance = noSpec ? null : rawResults[0]
const acCoverage = acceptance?.acCoverage ?? []

// Dedupe in code: same location+area merges, highest severity wins.
const SEV = { blocking: 0, important: 1, suggestion: 2 }
const byKey = new Map()
for (const res of rawResults.filter(Boolean)) {
  for (const f of res.findings) {
    const key = `${f.location.toLowerCase()}|${f.area}`
    const seen = byKey.get(key)
    if (!seen || SEV[f.severity] < SEV[seen.severity]) byKey.set(key, f)
  }
}
const merged = [...byKey.values()].sort((a, b) => SEV[a.severity] - SEV[b.severity])
log(`${merged.length} findings after dedupe, ${acCoverage.length} ACs evaluated`)

// ─── Phase: Verify ───
phase('Verify')
const findings = [], refuted = []
let maintainerFailed = false
let acDisputes = []
// Run the maintainer even when the lanes found nothing: an all-IMPLEMENTED,
// zero-findings AC table is exactly the result that needs an adversary.
if (merged.length > 0 || acCoverage.length > 0) {
  const numbered = merged.map((f, i) => ({ index: i, ...f }))
  // The maintainer needs the pack, not just the findings: "another ticket
  // covers it" is only testable against the pack's issue links, and AC
  // disputes need the full criterion text, not the table's short form.
  const specBlock = noSpec ? '' : `
Spec pack (the story's contract - its issue links and comments are your evidence for the "another ticket covers it" counter-argument; its full criterion texts back any AC dispute):
${input.specPack}`
  const acBlock = acCoverage.length === 0 ? '' : `
The acceptance lane's per-criterion verdicts follow. Challenge any verdict whose cited evidence does not hold when you read the code - a false IMPLEMENTED is the dangerous one, it passes a story that was not built. Return challenges in acDisputes (AC id + file:line-backed rationale). You never rewrite the table; a dispute is advisory and gates CONCERNS.
AC coverage: ${JSON.stringify(acCoverage)}`
  const res = await agent(
    `Repo root: ${scope.repoRoot}. QA target: ${target} (base: ${scope.base}). Story summary: ${scope.summary}${specBlock}
Apply the maintainer playbook at ${reviewRefs}/maintainer-playbook.md and the QA falsification counter-arguments at ${qaFalsification} in full. REFUTED requires concrete file:line evidence of a guard/test/design intent, a covering ticket, or an established convention. A finding that gets WORSE under your check is UPGRADED (set newSeverity) - those are the most valuable verdicts.
Re-verify every finding in this list and return one verdict per index:\n${JSON.stringify(numbered)}${acBlock}`,
    { agentType: 'dotnet-episteme-skills:review:maintainer', label: 'maintainer', phase: 'Verify', schema: VERDICTS_SCHEMA, ...modelOpt },
  )
  maintainerFailed = !res
  acDisputes = res?.acDisputes ?? []
  const verdicts = new Map((res?.verdicts ?? []).map(v => [v.index, v]))
  const SEV_UP = { suggestion: 'important', important: 'blocking', blocking: 'blocking' }
  for (const f of numbered) {
    const v = verdicts.get(f.index) ?? { verdict: 'CONFIRMED', rationale: 'maintainer unavailable; finding kept unfalsified' }
    if (v.verdict === 'REFUTED') { refuted.push({ location: f.location, area: f.area, rationale: v.rationale }); continue }
    const severity =
      v.verdict === 'DOWNGRADED' ? (v.newSeverity ?? 'suggestion')
      : v.verdict === 'UPGRADED' ? (v.newSeverity ?? SEV_UP[f.severity])
      : f.severity
    findings.push({ ...f, severity, counterCheck: v.rationale })
  }
  findings.sort((a, b) => SEV[a.severity] - SEV[b.severity])
  if (maintainerFailed) log('WARNING: the maintainer failed to run - findings kept unfalsified, gating CONCERNS')
  log(`${findings.length} findings survived, ${refuted.length} refuted${acDisputes.length ? `, ${acDisputes.length} AC verdicts disputed` : ''}`)
}

// Deterministic gate per the QA output contract. An empty/whitespace proving
// test counts as no proof, not as proven.
const noProof = ac => { const p = (ac.provingTest ?? '').trim(); return !p || /^none$/i.test(p) }
// Spec mode that evaluated zero criteria never verified the story - it cannot PASS.
const emptyAcInSpecMode = !noSpec && acCoverage.length === 0
if (emptyAcInSpecMode) log('WARNING: spec mode but the acceptance audit returned no criteria - gating CONCERNS')
const gate =
  acCoverage.some(ac => ac.verdict === 'MISSING') || findings.some(f => f.severity === 'blocking') ? 'FAIL'
  : noSpec || emptyAcInSpecMode || lanesFailed.length > 0 || maintainerFailed || acDisputes.length > 0 || acCoverage.some(ac => ac.verdict === 'PARTIAL' || noProof(ac)) || findings.some(f => f.severity === 'important') ? 'CONCERNS'
  : 'PASS'

// Skimmable human verdict line (the orchestrator renders it per the output contract).
const met = acCoverage.filter(ac => ac.verdict === 'IMPLEMENTED').length
const counts = { blocking: 0, important: 0, suggestion: 0 }
for (const f of findings) counts[f.severity]++
const missed = acCoverage.filter(ac => ac.verdict === 'MISSING').map(ac => ac.ac)
const verdictLine = noSpec
  ? 'No spec: acceptance not evaluated.'
  : acCoverage.length === 0 ? 'No acceptance criteria evaluated.'
  : missed.length ? `AC ${missed.join(', ')} missed.`
  : met === acCoverage.length ? `All ${acCoverage.length} ACs met.`
  : `${met} of ${acCoverage.length} ACs met.`

return { gate, verdictLine, acSummary: { met, total: acCoverage.length, counts }, noSpec, tier, scope: scope.summary, base: scope.base, acCoverage, acDisputes, findings, refuted, lanesFailed, maintainerFailed }
