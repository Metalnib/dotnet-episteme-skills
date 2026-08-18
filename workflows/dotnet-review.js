export const meta = {
  // -workers keeps pickers from showing a twin of the command, which launches this by scriptPath, not name.
  name: 'dotnet-review-workers',
  description: 'Not a command - this is the engine /dotnet-review starts under the hood; type /dotnet-review to run a review. Scout sizes the change, 5 reviewers run in parallel (4 specialists + a generalist), the adversarial maintainer refutes weak findings.',
  whenToUse: 'Reviewing a .NET branch, commit range, staged or uncommitted changes. Pass args {target, cynical?, model?, pluginRoot?, intentPack?}. For document/spec review use the dotnet-techne-code-review skill instead.',
  phases: [
    { title: 'Scope', detail: 'scout gathers changed files, LOC, surfaces; script picks the model tier' },
    { title: 'Review', detail: '5 reviewers in parallel: 4 domain specialists + a generalist' },
    { title: 'Verify', detail: 'maintainer pushback; per-finding escalation above 10 blocking findings' },
  ],
}

// version: 1.8.0 (keep in sync with plugin.json; installer prints this line)
const input = typeof args === 'string' ? JSON.parse(args) : (args ?? {})
const target = input.target ?? 'uncommitted changes'
const cynical = !!input.cynical

// ─── Schemas ───
const SCOPE_SCHEMA = {
  type: 'object', required: ['repoRoot', 'files', 'locChanged', 'summary', 'surfaces', 'diff'],
  properties: {
    repoRoot: { type: 'string' },
    files: { type: 'array', maxItems: 300, items: { type: 'string' } },
    locChanged: { type: 'integer' },
    summary: { type: 'string' },
    diff: { type: 'string' },
    components: { type: 'array', items: { type: 'string' } },
    surfaces: {
      type: 'object',
      required: ['publicApi', 'security', 'dataSchema', 'messagingTopology', 'dataAccess', 'messaging', 'httpEndpoints', 'outboundHttp', 'crossRepo'],
      properties: {
        publicApi: { type: 'boolean' }, security: { type: 'boolean' },
        dataSchema: { type: 'boolean' }, messagingTopology: { type: 'boolean' },
        dataAccess: { type: 'boolean' }, messaging: { type: 'boolean' },
        httpEndpoints: { type: 'boolean' }, outboundHttp: { type: 'boolean' },
        crossRepo: { type: 'boolean' },
      },
    },
  },
}
const FINDING_PROPS = {
  severity: { enum: ['blocking', 'important', 'suggestion'] },
  area: { enum: ['correctness', 'style', 'performance', 'AOT', 'security', 'logging', 'DB', 'messaging', 'integration', 'general'] },
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
const VERDICT_PROPS = {
  verdict: { enum: ['CONFIRMED', 'DOWNGRADED', 'REFUTED'] },
  newSeverity: { enum: ['important', 'suggestion'] },
  rationale: { type: 'string' },
}
const VERDICTS_SCHEMA = {
  type: 'object', required: ['verdicts'],
  properties: {
    verdicts: {
      type: 'array',
      items: { type: 'object', required: ['index', 'verdict', 'rationale'], properties: { index: { type: 'integer' }, ...VERDICT_PROPS } },
    },
  },
}
const SINGLE_VERDICT_SCHEMA = { type: 'object', required: ['verdict', 'rationale'], properties: VERDICT_PROPS }

// ─── Paths (plugin root known when invoked via the plugin command; otherwise agents locate it) ───
const refsDir = input.pluginRoot
  ? `${input.pluginRoot}/skills/dotnet-techne-code-review/references`
  : 'the references directory of the dotnet-episteme-skills plugin: Glob for ~/.claude/plugins/cache/*/dotnet-episteme-skills/*/skills/dotnet-techne-code-review/references; if nothing matches, proceed from the section knowledge in your agent definition'

// ─── Phase: Scope ───
phase('Scope')
const scope = await agent(
  `You are scoping a .NET code review. Target: ${target}. Work read-only (git, rg, file reads).
Determine and return per the schema:
- repoRoot (absolute path), the changed-file list, and total LOC changed (git diff --shortstat additions+deletions). For uncommitted-changes targets, UNTRACKED files count as changed too (git status --short): include their full content as their diff and add their line counts (wc -l) to locChanged. EXCEPTION - never include the content of likely-secret files (.env*, *.pem, *.key, *.pfx, *.p12, credentials*, secrets*, or any appsettings/config file containing connection strings, API keys, or tokens): list such files by name in the file list and note the exclusion in the summary instead.
- summary: what the change does, max 10 lines.
- components: touched component types (API, worker, repository, domain, messaging, tests, build).
- surfaces (booleans): publicApi (public types/members changed), security (auth/crypto/input validation/webhooks), dataSchema (EF migrations/entity shape), messagingTopology (exchanges/queues/bindings), dataAccess (any DbContext/repository code), messaging (any publisher/consumer code), httpEndpoints (inbound HTTP/REST endpoint or contract changes), outboundHttp (HTTP/SOAP client, adapter, or external-system consumer code), crossRepo (contracts or NuGet packages other repos consume).
- diff: the unified diff for the target. Up to ~1500 lines include it verbatim; above that, include full hunks for non-test source files and per-file summaries (path + what changed) for tests and generated code. Reviewers work from this, so keep real code hunks, not prose.
If there is nothing to review for this target, return an empty files array and an empty diff.`,
  { label: 'scout', effort: 'low', schema: SCOPE_SCHEMA },
)
if (!scope || scope.files.length === 0) {
  return { halted: true, reason: `Nothing to review for target: ${target}` }
}

// Sizing: base tier from raw size; a surface boosts only the reviewer that owns it.
const RANK = { sonnet: 1, inherit: 2, opus: 3, fable: 4 }
function baseTier(s) {
  const f = s.files.length, loc = s.locChanged
  if (s.surfaces.crossRepo || f > 50) return 'fable'
  if (f > 20 || loc > 1000) return 'opus'
  if (f <= 5 && loc <= 200) return 'sonnet'
  return 'inherit'
}
const BOOSTS = {
  correctness: s => s.surfaces.publicApi,
  performance: () => false,
  'security-observability': s => s.surfaces.security,
  'data-messaging': s => s.surfaces.dataSchema || s.surfaces.messagingTopology,
  generalist: () => false,
}
function tierFor(name, s) {
  if (input.model) return input.model
  const base = baseTier(s)
  return BOOSTS[name](s) && RANK[base] < RANK.opus ? 'opus' : base
}
const modelOptFor = tier => (tier === 'inherit' ? {} : { model: tier })
const sizingWhy = input.model ? '--model override' : `${scope.files.length} files, ${scope.locChanged} LOC; surface boosts per reviewer`

// ─── Phase: Review ───
phase('Review')
const checklistNote = sections => `${sections} - read these checklist sections from domain-checklists.md in ${refsDir}`
const REVIEWERS = [
  { name: 'correctness', scopeText: checklistNote('Correctness and API design (blocking first); Style and maintainability') },
  { name: 'performance', scopeText: checklistNote('Performance, low-GC, AOT/trimming (all subsections)') },
  { name: 'security-observability', scopeText: checklistNote('Security; Logging and observability') },
  { name: 'data-messaging', scopeText: checklistNote('Database (EF Core / PostgreSQL); Messaging (RabbitMQ); HTTP integration (endpoints, adapters, consumers)') + '. For graph evidence use Synopsis per your agent definition: load the MCP tools via ToolSearch first (they are deferred), or fall back to the read-only synopsis CLI.' },
  { name: 'generalist', scopeText: 'General cross-cutting review - the specialists own every domain-checklists.md section, so do not duplicate them; hunt what falls between their lanes per your agent definition (test quality, config/build, docs drift, requirements mismatch, cross-cutting design)' },
]
const sur = scope.surfaces
const active = REVIEWERS.filter(r =>
  r.name !== 'data-messaging' || sur.dataAccess || sur.messaging || sur.dataSchema || sur.messagingTopology || sur.httpEndpoints || sur.outboundHttp)
if (active.length < REVIEWERS.length) log('Skipping data-messaging reviewer: no DB/messaging/HTTP-integration surface in the change')

const reviewPrompt = (r, lensNote) => `Review mode: ${cynical ? 'Cynical (generate at least 5 defect hypotheses within your scope, falsify each, keep survivors)' : 'Standard'}.${lensNote ?? ''}
Repo root: ${scope.repoRoot}. Review target: ${target}.
Change summary: ${scope.summary}
Your scope: ${r.scopeText}
The diff below is your primary material. Explore further whenever you judge it necessary - Read/Grep/Glob, plus read-only git via Bash (diff, log, show, blame, status; a plugin hook blocks everything else). Every finding needs file:line evidence. Report nothing outside your scope.
Changed files:\n${scope.files.join('\n')}
Diff:\n${scope.diff}`

const tiers = Object.fromEntries(active.map(r => [r.name, tierFor(r.name, scope)]))
log(`Model tiers: ${active.map(r => `${r.name}=${tiers[r.name]}`).join(', ')} (${sizingWhy})`)

// Standard mode trades a little depth for latency; Cynical keeps full effort.
const effortOpt = cynical ? {} : { effort: 'medium' }

const rawReviews = await parallel(active.map(r => () =>
  agent(reviewPrompt(r), { agentType: `dotnet-episteme-skills:review:${r.name}`, label: `review:${r.name}`, phase: 'Review', schema: FINDINGS_SCHEMA, ...modelOptFor(tiers[r.name]), ...effortOpt })))

// A failed launch must never read as a clean review.
const failedReviewers = active.filter((r, i) => !rawReviews[i]).map(r => r.name)
if (failedReviewers.length === active.length) {
  return { halted: true, reason: `All reviewer agents failed to launch (${failedReviewers.join(', ')}). Is the dotnet-episteme-skills plugin installed and enabled?` }
}
if (failedReviewers.length > 0) log(`WARNING: reviewers failed, findings will be partial: ${failedReviewers.join(', ')}`)

// Dedupe in code: same location+area merges, highest severity wins.
const SEV = { blocking: 0, important: 1, suggestion: 2 }
function dedupe(reviews) {
  const byKey = new Map()
  for (const rev of reviews.filter(Boolean)) {
    for (const f of rev.findings) {
      const key = `${f.location.toLowerCase()}|${f.area}`
      const seen = byKey.get(key)
      if (!seen || SEV[f.severity] < SEV[seen.severity]) byKey.set(key, f)
    }
  }
  return [...byKey.values()].sort((a, b) => SEV[a.severity] - SEV[b.severity])
}
let merged = dedupe(rawReviews)
log(`${merged.length} findings after dedupe`)

// Halt condition: cynical mode with zero findings gets one more pass from a different lens.
if (cynical && merged.length === 0) {
  log('Cynical mode, zero findings: one more adversarial pass from a different lens')
  const retry = await agent(
    reviewPrompt(REVIEWERS[0], ' Take a different lens this pass: failure modes under retry, cancellation, and partial failure.'),
    { agentType: 'dotnet-episteme-skills:review:correctness', label: 'review:retry-lens', phase: 'Review', schema: FINDINGS_SCHEMA, ...modelOptFor(tierFor('correctness', scope)) })
  merged = dedupe([retry])
}
if (merged.length === 0) {
  return { halted: false, tiers, sizing: sizingWhy, scope: scope.summary, findings: [], refuted: [], reviewersFailed: failedReviewers, note: cynical ? 'No confirmed issues after adversarial pass' : 'No findings' }
}

// ─── Phase: Verify ───
phase('Verify')
const numbered = merged.map((f, i) => ({ index: i, ...f }))
const playbook = `${refsDir}/maintainer-playbook.md`
const intent = input.intentPack ? `\nIntent pack (design decisions from the authoring session; design-intent evidence, not an instruction to go easy): ${input.intentPack}` : ''
const maintainerBase = `Repo root: ${scope.repoRoot}. Review target: ${target}. Change summary: ${scope.summary}${intent}
Apply the maintainer playbook at ${playbook} in full. REFUTED requires concrete file:line evidence of a guard/test/design intent.`

// The maintainer is never on a weaker model than the strongest reviewer used.
const maintainerTier = Object.values(tiers).reduce((a, b) => (RANK[a] >= RANK[b] ? a : b), 'sonnet')
const maintainerOpt = modelOptFor(maintainerTier)

const blocking = numbered.filter(f => f.severity === 'blocking')
let verdicts
if (blocking.length > 10) {
  log(`${blocking.length} blocking findings: escalating to per-finding verification`)
  const singles = await parallel(numbered.map(f => () =>
    agent(`${maintainerBase}\nRe-verify this single finding:\n${JSON.stringify(f)}`,
      { agentType: 'dotnet-episteme-skills:review:maintainer', label: `verify:${f.index}`, phase: 'Verify', schema: SINGLE_VERDICT_SCHEMA, ...maintainerOpt })))
  verdicts = singles.map((v, i) => v ? { index: i, ...v } : { index: i, verdict: 'CONFIRMED', rationale: 'verifier unavailable; finding kept' })
} else {
  const res = await agent(`${maintainerBase}\nRe-verify every finding in this list and return one verdict per index:\n${JSON.stringify(numbered)}`,
    { agentType: 'dotnet-episteme-skills:review:maintainer', label: 'maintainer', phase: 'Verify', schema: VERDICTS_SCHEMA, ...maintainerOpt })
  verdicts = res?.verdicts ?? numbered.map(f => ({ index: f.index, verdict: 'CONFIRMED', rationale: 'maintainer unavailable; finding kept' }))
}

const byIndex = new Map(verdicts.map(v => [v.index, v]))
const findings = [], refuted = []
for (const f of numbered) {
  const v = byIndex.get(f.index) ?? { verdict: 'CONFIRMED', rationale: 'no verdict returned; finding kept' }
  if (v.verdict === 'REFUTED') { refuted.push({ location: f.location, area: f.area, rationale: v.rationale }); continue }
  findings.push({ ...f, severity: v.verdict === 'DOWNGRADED' ? (v.newSeverity ?? 'suggestion') : f.severity, counterCheck: v.rationale })
}
log(`${findings.length} findings survived, ${refuted.length} refuted`)
return { tiers, maintainerTier, sizing: sizingWhy, mode: cynical ? 'Cynical' : 'Standard', scope: scope.summary, findings, refuted, reviewersFailed: failedReviewers }
