export const meta = {
  // -workers keeps pickers from showing a twin of the command, which launches this by scriptPath, not name.
  name: 'dotnet-qa-workers',
  description: 'Not a command - this is the engine /dotnet-qa starts under the hood; type /dotnet-qa to run a story QA. It runs the scout, the 3 QA lanes in parallel (acceptance vs spec, reuse/design, dead code), the maintainer falsification, and computes the gate.',
  whenToUse: 'Invoked by the /dotnet-qa command after it resolved the spec - not standalone (spec discovery needs the user). Pass args {target, base?, specPack?, practicesPack?, model?, pluginRoot?}.',
  phases: [
    { title: 'Scope', detail: 'scout resolves the merge-base and the changed files; script builds the diff command' },
    { title: 'Audit', detail: '3 QA lanes in parallel: acceptance (spec mode), reuse/design, dead code' },
    { title: 'Verify', detail: 'maintainer falsification, then the deterministic gate' },
  ],
}

// version: 1.8.1 (keep in sync with plugin.json; installer prints this line)
const input = typeof args === 'string' ? JSON.parse(args) : (args ?? {})
const target = input.target ?? 'current branch'
const noSpec = !input.specPack

// ─── Schemas ───
// No diff field: the QA workers produce the diff themselves from the command the
// script assembles below, so its tens of thousands of tokens never pass through a
// model's output on the one serial phase. Deleted from properties, not just from
// required - left in place a scout keeps filling it.
const SCOPE_SCHEMA = {
  type: 'object', required: ['repoRoot', 'baseRef', 'leftSha', 'rightSha', 'files', 'secretPaths', 'summary'],
  properties: {
    repoRoot: { type: 'string' },
    baseRef: { type: 'string', description: 'symbolic name of the base the merge-base came from, e.g. origin/main' },
    leftSha: { type: 'string', description: 'the merge-base, a full hex SHA' },
    rightSha: { type: 'string', description: 'the target tip, a full hex SHA' },
    files: { type: 'array', maxItems: 300, items: { type: 'string' } },
    secretPaths: { type: 'array', maxItems: 40, items: { type: 'string' } },
    summary: { type: 'string' },
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
    // The diff is a command the lane has to run, and the guard only ever denies -
    // without this field a lane whose command was blocked can only answer with an
    // empty findings list, which reads as a clean lane.
    laneFailed: { type: 'boolean' },
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
    laneFailed: FINDINGS_SCHEMA.properties.laneFailed,
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

// ─── Diff command assembly (keep in sync with workflows/dotnet-review.js) ───
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
// QA keeps tests and ordinary generated files in every lane, so it needs only the
// always tier: secret content and EF snapshots.

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
Read as much as you need. Emit NO diff content: your answer carries resolved SHAs and file lists, never hunks and never a shell command. The QA lanes produce the diff themselves from a command this script assembles out of your SHAs, so a diff typed here is pure waste on the one phase nothing else runs beside.
Determine and return per the schema:
- repoRoot (absolute path).
- baseRef: ${input.base ? `${input.base}` : 'the base you compared against - the default branch (origin/HEAD, or main/master)'}.
- leftSha: the merge-base of the target with that base (git merge-base). rightSha: the target tip (git rev-parse). Both FULL hex SHAs, never ref names - a ref re-resolves per lane, so a commit landing mid-run would give two lanes different views.
- files: every file the story changed between those two SHAs, repository-root-relative. If more than 300 files changed, list the 300 most story-relevant and state the true total in the summary - never truncate silently.
- secretPaths: changed paths whose content must stay out of the lanes' diffs - .env* and *.env, *.pem, *.key, *.pfx, *.p12, credentials*, secrets*, and any appsettings/config file you see carrying a connection string, API key or token. Name them; never quote their content.
- summary: what the story changes, max 10 lines.
If there is nothing to review, return an empty files array.`,
  { label: 'scout', model: 'sonnet', effort: 'medium', schema: SCOPE_SCHEMA },
)
if (!scope) {
  return { halted: true, reason: `The scout returned no usable scope for target: ${target}. Re-run the QA.` }
}
if (scope.files.length === 0) {
  return { halted: true, reason: `Nothing to QA for target: ${target}` }
}

// ─── The diff command the QA lanes run themselves ───
if (!isSha(scope.leftSha) || !isSha(scope.rightSha)) {
  return { halted: true, reason: `The scout did not resolve "${target}" to commit SHAs (leftSha="${scope.leftSha}", rightSha="${scope.rightSha}"). The lanes build the diff from pinned SHAs and there is no safe fallback - a single endpoint silently folds in local edits, and a ref name can carry characters the git guard rejects. Re-run the QA.` }
}
const base = sha(scope.leftSha)
// Withheld from every lane and never trimmed: keeping secret content out is a
// guarantee, not an optimization, and one surviving snapshot floods every lane.
const secretPaths = [...new Set([...(scope.secretPaths ?? []), ...scope.files.filter(isSecretName)])]
const efSnapshots = scope.files.filter(isEfSnapshot)
const always = [...new Set([...secretPaths, ...efSnapshots])]
const widenedExclusions = always.filter(p => widen(p) !== p).map(p => `${p} -> ${widen(p)}`)
const diffCommand = diffCmd(`${base} ${sha(scope.rightSha)}`, always.map(p => excludeSpec(widen(p), true)))
const baseLabel = `${scope.baseRef || 'the default branch'} (${base.slice(0, 8)})`
if (widenedExclusions.length) log(`Exclusion widened to a wildcard: ${widenedExclusions.join(', ')}`)

// No runtime model sizing: every lane inherits the session model - the
// session tier is enough for these validations, and auto-escalating to
// fable doubles the lane cost. --model stays as the explicit override.
const tier = input.model ?? 'inherit'
const modelOpt = tier === 'inherit' ? {} : { model: tier }
if (input.model) log(`Model tier: ${tier} (--model override)`)
// No size ladder here, for the same reason there is no model sizing: the session
// tier is enough for these validations. --effort is the explicit override.
const EFFORT_STEPS = ['low', 'medium', 'high', 'xhigh', 'max']
const askedEffort = String(input.effort ?? '').trim().toLowerCase()
if (askedEffort && !EFFORT_STEPS.includes(askedEffort)) log(`WARNING: ignoring effort "${input.effort}" - expected one of ${EFFORT_STEPS.join(', ')}`)
const effortOpt = EFFORT_STEPS.includes(askedEffort) ? { effort: askedEffort } : {}
if (effortOpt.effort) log(`Reasoning effort: ${effortOpt.effort} (--effort override)`)

// ─── Phase: Audit ───
phase('Audit')
const withheld = [
  secretPaths.length ? `Secret-classified paths, changed with their content withheld from your diff:\n${secretPaths.join('\n')}\nWithheld means not pushed into every lane's context by default, not off limits: Read one when your lane needs it. A changed secret path is itself reportable - a credential this story commits is a finding. Being unable to see one is not: state it as an uncertainty, never as a finding, and never as a blocking one. If withholding leaves your diff empty, that is a valid result, not a failed command.` : '',
  efSnapshots.length ? `EF whole-model snapshots are excluded from every lane's diff, the migration's own .cs is not: ${efSnapshots.join(', ')}` : '',
  widenedExclusions.length ? `These exclusions had to be widened to a wildcard, so a same-length neighbour may be missing from your diff: ${widenedExclusions.join(', ')}` : '',
].filter(Boolean).join('\n')

const common = `Repo root: ${scope.repoRoot}. Story target: ${target} (base: ${baseLabel}).
Story summary: ${scope.summary}
Produce your primary material FIRST, by running exactly this read-only command through Bash:
${diffCommand}
Run it verbatim. The flags neutralize local git config that would otherwise mangle or empty the output, and the pathspecs are what scopes your diff - do not rewrite the command, do not drop pathspecs, do not fall back to a plain diff of the target. If it is blocked or fails, set laneFailed true in your answer and say what happened; never return an empty findings list from a diff you could not read, and never substitute your own diff command.
Then explore further whenever you judge it necessary. Your Bash allows read-only git (diff, log, show, blame, status, rev-parse, merge-base), the synopsis CLI, and the read-only search and list tools (rg, grep, fd, find, ls, cat, head, tail, wc) on paths inside the project. Shell operators, pipes and redirection are blocked, as is each tool's own write or exec flag, so keep every command a single plain invocation. The command above is already anchored to the repository root, so do not add a -C option to it. Every finding needs file:line evidence. Report nothing outside your lane.
${withheld ? `${withheld}\n` : ''}Changed files:\n${scope.files.join('\n')}`

const LANES = [
  ...(noSpec ? [] : [{
    name: 'acceptance',
    schema: ACCEPTANCE_SCHEMA,
    prompt: `Spec pack (the story's contract - verify the implementation against it):\n${input.specPack}
When an AC's only evidence sits in a file whose content was withheld from your diff and you did not open it, verdict PARTIAL and state the uncertainty - never MISSING. MISSING gates the run FAIL, which would fail a correct implementation over our own redaction.\n${common}`,
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
  agent(l.prompt, { agentType: `dotnet-episteme-skills:qa:${l.name}`, label: `qa:${l.name}`, phase: 'Audit', schema: l.schema, ...modelOpt, ...effortOpt })))

// A lane that could not produce its diff is a failed lane, not a clean one.
const lanesFailed = LANES.filter((l, i) => !rawResults[i] || rawResults[i].laneFailed).map(l => l.name)
if (lanesFailed.length === LANES.length) {
  return { halted: true, reason: `Every QA lane failed (${lanesFailed.join(', ')}) - they either did not launch or could not run their diff command. Check that the dotnet-episteme-skills plugin is installed and enabled, and that Bash git commands are permitted for its worker agents.` }
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
    `Repo root: ${scope.repoRoot}. QA target: ${target} (base: ${baseLabel}). Story summary: ${scope.summary}${specBlock}
The change itself is one read-only command away - run it verbatim through Bash when you need the diff:
${diffCommand}
${withheld ? `${withheld}\n` : ''}If that command is blocked or fails, say so in every rationale and keep the findings; never REFUTE one for lack of evidence you could not go and get.
Apply the maintainer playbook at ${reviewRefs}/maintainer-playbook.md and the QA falsification counter-arguments at ${qaFalsification} in full. REFUTED requires concrete file:line evidence of a guard/test/design intent, a covering ticket, or an established convention. A finding that gets WORSE under your check is UPGRADED (set newSeverity) - those are the most valuable verdicts.
Re-verify every finding in this list and return one verdict per index:\n${JSON.stringify(numbered)}${acBlock}`,
    { agentType: 'dotnet-episteme-skills:review:maintainer', label: 'maintainer', phase: 'Verify', schema: VERDICTS_SCHEMA, ...modelOpt, ...effortOpt },
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

return { gate, verdictLine, acSummary: { met, total: acCoverage.length, counts }, noSpec, tier, effort: effortOpt.effort ?? 'session default', scope: scope.summary, base, baseRef: scope.baseRef, baseLabel, acCoverage, acDisputes, findings, refuted, lanesFailed, maintainerFailed }
