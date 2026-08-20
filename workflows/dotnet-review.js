export const meta = {
  // -workers keeps pickers from showing a twin of the command, which launches this by scriptPath, not name.
  name: 'dotnet-review-workers',
  description: 'Not a command - this is the engine /dotnet-review starts under the hood; type /dotnet-review to run a review. Scout resolves the change scope, up to 5 reviewers run in parallel (4 specialists + a generalist), the adversarial maintainer refutes weak findings.',
  whenToUse: 'Reviewing a .NET branch, commit range, staged or uncommitted changes. Pass args {target, cynical?, model?, pluginRoot?, intentPack?}. For document/spec review use the dotnet-techne-code-review skill instead.',
  phases: [
    { title: 'Scope', detail: 'scout resolves SHAs, changed files, LOC, surfaces; script builds the diff command and picks the model tier' },
    { title: 'Review', detail: 'up to 5 reviewers in parallel: 4 domain specialists + a generalist' },
    { title: 'Verify', detail: 'maintainer pushback; per-finding escalation above 10 blocking findings' },
  ],
}

// version: 1.8.1 (keep in sync with plugin.json; installer prints this line)
const input = typeof args === 'string' ? JSON.parse(args) : (args ?? {})
const target = input.target ?? 'uncommitted changes'
const cynical = !!input.cynical
// Effort ladder: same file/LOC breakpoints as the model tier, so there is one
// sizing story. Thinking is the cheap lever now that opus caps the model tier.
const EFFORT_STEPS = ['low', 'medium', 'high', 'xhigh', 'max']
const askedEffort = String(input.effort ?? '').trim().toLowerCase()

// ─── Schemas ───
// No diff field: the workers produce the diff themselves from the command the
// script assembles below, so its tens of thousands of tokens never pass through
// a model's output on the one serial phase. Deleted from properties, not just
// from required - left in place a scout keeps filling it.
const SCOPE_SCHEMA = {
  type: 'object',
  required: ['repoRoot', 'targetKind', 'leftSha', 'rightSha', 'files', 'sourceFiles', 'testFiles', 'generatedFiles', 'secretPaths', 'locChanged', 'summary', 'surfaces'],
  properties: {
    repoRoot: { type: 'string' },
    targetKind: { enum: ['branch', 'range', 'commit', 'staged', 'uncommitted'] },
    leftSha: { type: 'string', description: 'left endpoint, always a full hex SHA' },
    rightSha: { type: 'string', description: 'right endpoint; empty for staged and uncommitted' },
    files: { type: 'array', maxItems: 300, items: { type: 'string' } },
    sourceFiles: { type: 'array', maxItems: 300, items: { type: 'string' } },
    testFiles: { type: 'array', maxItems: 300, items: { type: 'string' } },
    generatedFiles: { type: 'array', maxItems: 300, items: { type: 'string' } },
    testDirs: { type: 'array', maxItems: 60, items: { type: 'string' } },
    secretPaths: { type: 'array', maxItems: 40, items: { type: 'string' } },
    untrackedSafe: { type: 'array', maxItems: 100, items: { type: 'string' } },
    untrackedExcluded: { type: 'array', maxItems: 40, items: { type: 'string' } },
    locChanged: { type: 'integer' },
    summary: { type: 'string' },
    surfaceEvidence: { type: 'array', maxItems: 12, items: { type: 'string' }, description: 'one "surface: path" line per surface returned true' },
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
    // The diff is a command the worker has to run, and the guard only ever denies -
    // without this field a worker whose command was blocked can only answer with an
    // empty findings list, which reads as a clean lane.
    laneFailed: { type: 'boolean' },
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

// ─── Diff command assembly (keep in sync with workflows/dotnet-qa.js) ───
// The script owns this syntax, never the model: an unquoted space turns the tail
// into a second POSITIVE pathspec, and a cwd-relative exclusion silently lets the
// excluded files back into the diff. The flags neutralize local git config that
// would otherwise break a worker's output - diff.external replaces unified output,
// diff.noprefix breaks path parsing, diff.relative returns an EMPTY diff when the
// worker's cwd subtree is untouched. `git -c` is not available: the guard hook at
// hooks/git-readonly-guard.sh admits -C only, so every flag is per-subcommand.
const GIT_DIFF = 'git diff --no-ext-diff --no-color --no-relative --ignore-submodules=none --src-prefix=a/ --dst-prefix=b/'
const sha = v => String(v ?? '').trim().toLowerCase()
// Full length only. A 7-char hex string can be a branch name, and git resolves the
// ref, so a short SHA silently un-pins the diff instead of failing loudly.
const isSha = v => /^[0-9a-f]{40,64}$/.test(sha(v))
// The guard scans the raw command string, quotes included, so a path that reads as
// one of git's write flags gets the whole command rejected for every worker.
const readsAsFlag = s => /(^|\s)(-o|--output|--no-index)(\s|=|$)/.test(s)
// A path carrying a shell operator cannot be quoted into the command at all - the
// guard rejects the whole command on one such character - so widen it instead of
// dropping the exclusion. '?' and not '*': it matches the original character for
// character, so the file stays excluded and only same-length siblings go with it,
// where '*' also crosses directory boundaries. Brackets have to go too, because
// inside a [...] class a wildcard is just a literal member and the exclusion
// silently stops matching the file it names.
const widen = p => {
  const w = p.replace(/[;&|<>`$'\\[\]\r\n]/g, '?')
  return readsAsFlag(w) ? w.replace(/\s/g, '?') : w
}
// The same name patterns the scout is given, as a code-side floor under its answer:
// a scout that misses or misspells one cannot un-withhold a file the code can see.
// Content-based secrets (a config carrying a connection string) stay its job.
const isSecretName = p => /(^|\/)(\.env|credentials|secrets)[^/]*$/i.test(p) || /\.(env|pem|key|pfx|p12)$/i.test(p)
// EF whole-model snapshots run to tens of thousands of lines nobody reads, so they
// are excluded for every worker. The migration's own .cs stays visible to all.
const isEfSnapshot = p => /ModelSnapshot\.cs$/i.test(p) || (/\.Designer\.cs$/i.test(p) && p.split('/').includes('Migrations'))
// icase on the always tier: pathspec matching is byte-exact, so a secret path the
// scout spelled with the wrong case would otherwise keep its content in every diff.
const excludeSpec = (p, icase) => `:(top,exclude${icase ? ',icase' : ''})${p}`
// ':/' is the repository root. `-- .` would silently narrow the diff to the
// worker's own subtree, so when there is nothing to exclude the clause is dropped.
const diffCmd = (endpoints, specs) => specs.length ? `${GIT_DIFF} ${endpoints} -- ':/' ${specs.map(x => `'${x}'`).join(' ')}` : `${GIT_DIFF} ${endpoints}`

// ─── Paths (plugin root known when invoked via the plugin command; otherwise agents locate it) ───
const refsDir = input.pluginRoot
  ? `${input.pluginRoot}/skills/dotnet-techne-code-review/references`
  : 'the references directory of the dotnet-episteme-skills plugin: Glob for ~/.claude/plugins/cache/*/dotnet-episteme-skills/*/skills/dotnet-techne-code-review/references; if nothing matches, proceed from the section knowledge in your agent definition'

// ─── Phase: Scope ───
phase('Scope')
const scope = await agent(
  `You are scoping a .NET code review. Target: ${target}. Work read-only (git, rg, file reads).
Read as much as you need. Emit NO diff content: your answer carries resolved SHAs and file lists, never hunks and never a shell command. The reviewers produce the diff themselves from a command this script assembles out of your SHAs, so a diff typed here is pure waste on the one phase nothing else runs beside.
Determine and return per the schema:
- repoRoot (absolute path).
- targetKind, leftSha, rightSha: resolve the target to FULL hex SHAs, never ref names. A ref re-resolves per reviewer, so a commit landing mid-run would give two reviewers different views.
  * a branch -> targetKind 'branch', leftSha = git merge-base <branch> <default branch>, rightSha = git rev-parse <branch>.
  * a range typed A..B -> 'range', leftSha = git rev-parse A, rightSha = git rev-parse B.
  * a range typed A...B -> 'range', leftSha = git merge-base A B, rightSha = git rev-parse B.
  * a single commit -> 'commit', rightSha = that commit, leftSha = its first parent (for a root commit use the empty tree: git hash-object -t tree /dev/null).
  * --staged -> 'staged', leftSha = git rev-parse HEAD, rightSha = '' (empty).
  * uncommitted changes -> 'uncommitted', leftSha = git rev-parse HEAD, rightSha = '' (empty).
- files: every changed path, repository-root-relative. For uncommitted-changes targets UNTRACKED files count as changed too (git status --porcelain -uall; plain --short collapses a new directory to one entry and hides the files under it). If more than 300 files changed, list the 300 most review-relevant and state the true total in the summary - never truncate silently.
- locChanged: git diff --shortstat additions+deletions, plus wc -l of each untracked file on uncommitted targets.
- sourceFiles / testFiles / generatedFiles: partition files, every path in exactly one list. Generated means *.g.cs, *.generated.cs, *ModelSnapshot.cs and *.Designer.cs. An EF migration's own <timestamp>_<Name>.cs is SOURCE, not generated.
- testDirs: repository-root-relative directory prefixes of test projects (tests/Foo.Tests, src/Foo.Tests), deduped to the shallowest directory that covers each project. A test file outside all of them stays in testFiles.
- secretPaths: changed paths whose content must stay out of the reviewers' diffs - .env* and *.env, *.pem, *.key, *.pfx, *.p12, credentials*, secrets*, and any appsettings/config file you see carrying a connection string, API key or token. Name them; never quote their content.
- untrackedSafe / untrackedExcluded: the untracked files split by that same secret rule. Uncommitted targets only, both empty otherwise.
- summary: what the change does, max 10 lines.
- surfaces (booleans): publicApi (public types/members changed), security (auth/crypto/input validation/webhooks), dataSchema (EF migrations/entity shape), messagingTopology (exchanges/queues/bindings), dataAccess (any DbContext/repository code), messaging (any publisher/consumer code), httpEndpoints (inbound HTTP/REST endpoint or contract changes), outboundHttp (HTTP/SOAP client, adapter, or external-system consumer code), crossRepo (contracts or NuGet packages other repos consume). When you are not sure, return TRUE: an uncertain surface costs one extra reviewer, a missed one is a silent gap. crossRepo is the ONE exception - it raises every reviewer's model tier, so return it true only when you can name a non-documentation file another repository actually consumes (a published contract, DTO or packaged library). A README, doc or comment change never qualifies, and neither does a type that only this repository uses.
- surfaceEvidence: one line per surface you returned true, formatted 'surface: path/that/made/it/true.cs'.
If there is nothing to review for this target, return an empty files array.`,
  { label: 'scout', model: 'sonnet', effort: 'medium', schema: SCOPE_SCHEMA },
)
if (!scope) {
  return { halted: true, reason: `The scout returned no usable scope for target: ${target}. Re-run the review.` }
}
if (scope.files.length === 0) {
  return { halted: true, reason: `Nothing to review for target: ${target}` }
}

// ─── The diff commands the reviewers run themselves ───
const needsRight = scope.targetKind !== 'staged' && scope.targetKind !== 'uncommitted'
if (!isSha(scope.leftSha) || (needsRight && !isSha(scope.rightSha))) {
  return { halted: true, reason: `The scout did not resolve "${target}" to commit SHAs (targetKind=${scope.targetKind}, leftSha="${scope.leftSha}", rightSha="${scope.rightSha}"). Reviewers build the diff from pinned SHAs and there is no safe fallback - a single endpoint silently folds in local edits, and a ref name can carry characters the git guard rejects. Re-run the review.` }
}
// A working-tree target has no right endpoint, so a rightSha here means the scout
// contradicted itself. Guessing either way is wrong: keep the kind and we may fold
// in local edits, keep the SHA and we may diff a commit against itself.
if (!needsRight && sha(scope.rightSha)) {
  return { halted: true, reason: `The scout returned targetKind=${scope.targetKind} together with rightSha="${scope.rightSha}", which contradict each other. Re-run the review.` }
}
// `git diff <sha>` alone compares against the WORKING TREE: exactly what an
// uncommitted target wants, and exactly what a branch or range must never do.
const endpoints =
  scope.targetKind === 'staged' ? `--cached ${sha(scope.leftSha)}`
  : scope.targetKind === 'uncommitted' ? sha(scope.leftSha)
  : `${sha(scope.leftSha)} ${sha(scope.rightSha)}`

const files = scope.files
const sourceFiles = scope.sourceFiles ?? []
const secretPaths = [...new Set([...(scope.secretPaths ?? []), ...files.filter(isSecretName)])]
const uncommitted = scope.targetKind === 'uncommitted'

// Excluded for every reviewer and never trimmed: withholding secret content is a
// guarantee, not an optimization, and one surviving snapshot re-floods everyone.
const always = [...new Set([...secretPaths, ...files.filter(isEfSnapshot)])]
const alwaysSpecs = always.map(p => excludeSpec(widen(p), true))
const widenedExclusions = always.filter(p => widen(p) !== p).map(p => `${p} -> ${widen(p)}`)

// Specialists only: tests and ordinary generated code. A path that cannot be
// quoted is dropped here rather than widened - over-excluding in this tier could
// hide a source file, while an unfiltered test file is only noise.
const MAX_PATHSPECS = 150
const declaredTestDirs = (scope.testDirs ?? []).map(d => d.replace(/\/+$/, '')).filter(Boolean)
const covers = (d, p) => p === d || p.startsWith(`${d}/`)
// A test directory that also holds source files would wipe the specialists' diff,
// and the empty-is-valid note below would make four dead lanes read as clean.
const overreachingDirs = declaredTestDirs.filter(d => sourceFiles.some(p => covers(d, p)))
const testDirs = declaredTestDirs.filter(d => !overreachingDirs.includes(d))
if (overreachingDirs.length) log(`Ignoring test directories that also hold source files: ${overreachingDirs.join(', ')}`)
const inTestDir = p => testDirs.some(d => covers(d, p))
const alwaysSet = new Set(always)
const candidates = [
  ...testDirs.map(d => ({ path: d, spec: excludeSpec(`${d}/**`), label: `${d}/` })),
  ...[
    ...(scope.testFiles ?? []).filter(p => !inTestDir(p)),
    ...(scope.generatedFiles ?? []).filter(p => !alwaysSet.has(p)),
  ].map(p => ({ path: p, spec: excludeSpec(p), label: p })),
]
const quotable = c => widen(c.path) === c.path && !/[*?[\]]/.test(c.path) && !readsAsFlag(c.path)
const usable = candidates.filter(quotable)
const room = Math.max(0, MAX_PATHSPECS - alwaysSpecs.length)
const kept = usable.slice(0, room)
const skipByEye = [...overreachingDirs.map(d => `${d}/ (test directory that also holds source)`), ...candidates.filter(c => !quotable(c)).map(c => c.label), ...usable.slice(room).map(c => c.label)]

const diffCommand = diffCmd(endpoints, alwaysSpecs)
const diffCommandFiltered = diffCmd(endpoints, [...alwaysSpecs, ...kept.map(c => c.spec)])
if (skipByEye.length) log(`${skipByEye.length} test/generated paths could not be excluded (pathspec cap, unquotable path, or an overreaching test directory); the specialists are told to skip them by eye`)
if (widenedExclusions.length) log(`Exclusion widened to a wildcard: ${widenedExclusions.join(', ')}`)

// Fail open in code for what a path already proves: a shallow scout read costs one
// extra reviewer instead of a silent gap.
const inferred = []
// crossRepo is the one surface where a wrong TRUE is expensive rather than cheap:
// it sends every reviewer AND the maintainer to the top model tier with no size
// gate, so unlike the others it has to earn its evidence. A doc file, or a path
// the scout did not even list as changed, is not a contract another repo consumes.
const evidenceFor = key => {
  const line = (scope.surfaceEvidence ?? []).find(l => l.trim().toLowerCase().startsWith(`${key.toLowerCase()}:`))
  return line ? line.slice(line.indexOf(':') + 1).trim() : ''
}
const fileSet = new Set(files)
if (scope.surfaces.crossRepo) {
  const ev = evidenceFor('crossRepo')
  const why = !ev ? 'no evidence file was named'
    : /\.(md|markdown|txt|rst|adoc)$/i.test(ev) ? `its evidence "${ev}" is documentation`
    : !fileSet.has(ev) ? `its evidence "${ev}" is not in the changed-file list`
    : ''
  if (why) {
    scope.surfaces.crossRepo = false
    log(`crossRepo dropped to false: ${why}. Model tiers come from the change size alone.`)
  }
}
const strayEvidence = (scope.surfaceEvidence ?? [])
  .map(l => l.slice(l.indexOf(':') + 1).trim())
  .filter(ev => ev && !fileSet.has(ev))
if (strayEvidence.length) log(`WARNING: surface evidence names files that are not in the change: ${strayEvidence.join(', ')}`)

const visibleFiles = files.filter(p => !alwaysSet.has(p))
const infer = (key, pred) => { if (!scope.surfaces[key] && visibleFiles.some(pred)) { scope.surfaces[key] = true; inferred.push(key) } }
infer('dataSchema', p => p.split('/').includes('Migrations'))
infer('dataAccess', p => /DbContext|Repository/i.test(p))
infer('messaging', p => /Consumer|Publisher|Producer/i.test(p))
infer('httpEndpoints', p => /Controller|Endpoint/i.test(p))
infer('outboundHttp', p => /HttpClient|Adapter|Gateway/i.test(p))
if (inferred.length) log(`Surfaces set from paths (fail open): ${inferred.join(', ')}`)
if (scope.surfaceEvidence?.length) log(`Surface evidence: ${scope.surfaceEvidence.join('; ')}`)

// Sizing: base tier from raw size; a surface boosts only the reviewer that owns it.
// Opus is the ceiling the script will ever pick on its own. Fable costs several
// times an opus reviewer and this fans out six agents, so it runs only when the
// user asks for it with --model fable.
const RANK = { sonnet: 1, inherit: 2, opus: 3, fable: 4 }
function baseTier(s) {
  const f = s.files.length, loc = s.locChanged
  if (s.surfaces.crossRepo || f > 20 || loc > 1000) return 'opus'
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

function baseEffort(s) {
  const f = s.files.length, loc = s.locChanged
  if (f > 50 || loc > 3000) return 'xhigh'
  if (f > 20 || loc > 1000) return 'high'
  return 'medium'
}
const bumped = e => EFFORT_STEPS[Math.min(EFFORT_STEPS.indexOf(e) + 1, EFFORT_STEPS.length - 1)]
if (askedEffort && !EFFORT_STEPS.includes(askedEffort)) log(`WARNING: ignoring effort "${input.effort}" - expected one of ${EFFORT_STEPS.join(', ')}`)
// Cynical asks every reviewer to build and falsify hypotheses, which is exactly
// what the extra step pays for.
const effort = EFFORT_STEPS.includes(askedEffort) ? askedEffort
  : cynical ? bumped(baseEffort(scope))
  : baseEffort(scope)
const effortWhy = EFFORT_STEPS.includes(askedEffort) ? '--effort override'
  : cynical ? `${baseEffort(scope)} for the size, one step up for Cynical`
  : 'from the change size'
// The maintainer is never given less thinking than the reviewers it re-verifies.
const effortOpt = { effort }

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
// Only tests and generated files changed: the 4 specialists have nothing in their
// lane. The generalist runs regardless - test quality is its lane.
const noSource = sourceFiles.length === 0
const reviewersSkipped = []
const active = REVIEWERS.filter(r => {
  if (noSource && r.name !== 'generalist') {
    reviewersSkipped.push(`${r.name} (the change has no source files - tests and generated code only)`)
    return false
  }
  if (r.name === 'data-messaging' && !(sur.dataAccess || sur.messaging || sur.dataSchema || sur.messagingTopology || sur.httpEndpoints || sur.outboundHttp)) {
    reviewersSkipped.push('data-messaging (no DB, messaging or HTTP-integration surface in the change)')
    return false
  }
  return true
})
if (reviewersSkipped.length) log(`Skipped reviewers: ${reviewersSkipped.join('; ')}`)

const untrackedSafe = uncommitted ? (scope.untrackedSafe ?? []) : []
const untrackedExcluded = uncommitted ? (scope.untrackedExcluded ?? []) : []
const sourceSet = new Set(sourceFiles)
const efSnapshots = files.filter(isEfSnapshot)

const reviewPrompt = (r, lensNote) => {
  const specialist = r.name !== 'generalist'
  // A specialist's file lists have to match its filtered diff, or an untracked test
  // file arrives as required reading and the lists contradict the diff.
  const untracked = specialist ? untrackedSafe.filter(p => sourceSet.has(p)) : untrackedSafe
  const notes = []
  if (specialist) notes.push('Tests and ordinary generated files are filtered out of your diff on purpose - the generalist owns test quality. Two consequences: a file MOVED from a test directory into source shows as a bare add, because its rename source is filtered out (not "new code with no history"); and an empty diff means nothing in your lane, not a failed command.')
  if (specialist && skipByEye.length) notes.push(`These test or generated paths could not be filtered out of your diff - skip them by eye:\n${skipByEye.join('\n')}`)
  if (untracked.length) notes.push(`Untracked files are part of this change and appear in NO git diff. Read them with your file tools:\n${untracked.join('\n')}`)
  if (untrackedExcluded.length) notes.push(`Untracked and secret-classified - changed, content withheld:\n${untrackedExcluded.join('\n')}`)
  if (secretPaths.length) notes.push(`Secret-classified paths, changed with their content withheld from your diff:\n${secretPaths.join('\n')}\nWithheld means not pushed into every reviewer's context by default, not off limits: Read one when your lane needs it. A changed secret path is itself reportable - a credential this change commits is a finding. Being unable to see one is not: state it as an uncertainty, never as a finding. If withholding leaves your diff empty, that is a valid result, not a failed command.`)
  if (efSnapshots.length) notes.push(`EF whole-model snapshots are excluded from every reviewer's diff, the migration's own .cs is not: ${efSnapshots.join(', ')}`)
  if (widenedExclusions.length) notes.push(`These exclusions had to be widened to a wildcard, so a same-length neighbour may be missing from your diff: ${widenedExclusions.join(', ')}`)
  return `Review mode: ${cynical ? 'Cynical (generate at least 5 defect hypotheses within your scope, falsify each, keep survivors)' : 'Standard'}.${lensNote ?? ''}
Repo root: ${scope.repoRoot}. Review target: ${target}.
Change summary: ${scope.summary}
Your scope: ${r.scopeText}
Produce your primary material FIRST, by running exactly this read-only command through Bash:
${specialist ? diffCommandFiltered : diffCommand}
Run it verbatim. The flags neutralize local git config that would otherwise mangle or empty the output, and the pathspecs are what scopes your diff - do not rewrite the command, do not drop pathspecs, do not fall back to a plain diff of the target. If it is blocked or fails, set laneFailed true in your answer and say what happened; never return an empty findings list from a diff you could not read, and never substitute your own diff command.
Then explore further whenever you judge it necessary. Your Bash allows read-only git (diff, log, show, blame, status, rev-parse, merge-base), the synopsis CLI, and the read-only search and list tools (rg, grep, fd, find, ls, cat, head, tail, wc) on paths inside the project. Shell operators, pipes and redirection are blocked, as is each tool's own write or exec flag, so keep every command a single plain invocation. The command above is already anchored to the repository root, so do not add a -C option to it. Every finding needs file:line evidence. Report nothing outside your scope.
${notes.length ? `${notes.join('\n')}\n` : ''}Changed files:\n${(specialist ? sourceFiles : files).join('\n')}`
}

const tiers = Object.fromEntries(active.map(r => [r.name, tierFor(r.name, scope)]))
log(`Model tiers: ${active.map(r => `${r.name}=${tiers[r.name]}`).join(', ')} (${sizingWhy})`)
log(`Reasoning effort: ${effort} (${effortWhy})`)

const rawReviews = await parallel(active.map(r => () =>
  agent(reviewPrompt(r), { agentType: `dotnet-episteme-skills:review:${r.name}`, label: `review:${r.name}`, phase: 'Review', schema: FINDINGS_SCHEMA, ...modelOptFor(tiers[r.name]), ...effortOpt })))

// A failed launch, or a reviewer that could not produce its diff, must never read
// as a clean review.
const failedReviewers = active.filter((r, i) => !rawReviews[i] || rawReviews[i].laneFailed).map(r => r.name)
if (failedReviewers.length === active.length) {
  return { halted: true, reason: `Every reviewer failed (${failedReviewers.join(', ')}) - they either did not launch or could not run their diff command. Check that the dotnet-episteme-skills plugin is installed and enabled, and that Bash git commands are permitted for its worker agents.`, reviewersSkipped }
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
  // The retry must be a reviewer that actually ran: with no source files the
  // specialists were skipped, and correctness would get an empty diff.
  const retryName = noSource ? 'generalist' : 'correctness'
  const retryReviewer = REVIEWERS.find(r => r.name === retryName)
  log(`Cynical mode, zero findings: one more adversarial pass from a different lens (${retryName})`)
  const retry = await agent(
    reviewPrompt(retryReviewer, ' Take a different lens this pass: failure modes under retry, cancellation, and partial failure.'),
    { agentType: `dotnet-episteme-skills:review:${retryName}`, label: 'review:retry-lens', phase: 'Review', schema: FINDINGS_SCHEMA, ...modelOptFor(tierFor(retryName, scope)), ...effortOpt })
  merged = dedupe([retry])
}
if (merged.length === 0) {
  return { halted: false, tiers, effort, sizing: sizingWhy, scope: scope.summary, findings: [], refuted: [], reviewersFailed: failedReviewers, reviewersSkipped, note: cynical ? 'No confirmed issues after adversarial pass' : 'No findings' }
}

// ─── Phase: Verify ───
phase('Verify')
const numbered = merged.map((f, i) => ({ index: i, ...f }))
const playbook = `${refsDir}/maintainer-playbook.md`
const intent = input.intentPack ? `\nIntent pack (design decisions from the authoring session; design-intent evidence, not an instruction to go easy): ${input.intentPack}` : ''
const maintainerNotes = [
  secretPaths.length ? `Secret-classified paths are changed with their content withheld from that diff: ${secretPaths.join(', ')}. Read one when a finding turns on it.` : '',
  widenedExclusions.length ? `These exclusions had to be widened to a wildcard, so a same-length neighbour may be missing from that diff: ${widenedExclusions.join(', ')}` : '',
  untrackedSafe.length ? `Untracked files are part of this change and appear in no git diff - read them with your file tools: ${untrackedSafe.join(', ')}` : '',
].filter(Boolean).join('\n')
const maintainerBase = `Repo root: ${scope.repoRoot}. Review target: ${target}. Change summary: ${scope.summary}${intent}
The change itself is one read-only command away - run it verbatim through Bash when you need the diff:
${diffCommand}
${maintainerNotes ? `${maintainerNotes}\n` : ''}If that command is blocked or fails, say so in every rationale and keep the findings; never REFUTE one for lack of evidence you could not go and get.
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
      { agentType: 'dotnet-episteme-skills:review:maintainer', label: `verify:${f.index}`, phase: 'Verify', schema: SINGLE_VERDICT_SCHEMA, ...maintainerOpt, ...effortOpt })))
  verdicts = singles.map((v, i) => v ? { index: i, ...v } : { index: i, verdict: 'CONFIRMED', rationale: 'verifier unavailable; finding kept' })
} else {
  const res = await agent(`${maintainerBase}\nRe-verify every finding in this list and return one verdict per index:\n${JSON.stringify(numbered)}`,
    { agentType: 'dotnet-episteme-skills:review:maintainer', label: 'maintainer', phase: 'Verify', schema: VERDICTS_SCHEMA, ...maintainerOpt, ...effortOpt })
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
return { tiers, maintainerTier, effort, sizing: sizingWhy, mode: cynical ? 'Cynical' : 'Standard', scope: scope.summary, findings, refuted, reviewersFailed: failedReviewers, reviewersSkipped }
