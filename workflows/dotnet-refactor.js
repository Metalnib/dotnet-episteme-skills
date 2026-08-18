export const meta = {
  // -workers keeps pickers from showing a twin of the command, which launches this by scriptPath, not name.
  name: 'dotnet-refactor-workers',
  description: 'Not a command - this is the engine /dotnet-refactor starts under the hood; type /dotnet-refactor to run the design loop. It fans out the cartographers and tracers (phase "map"; lite mode runs one surveyor), or the conformance auditor (phase "audit").',
  whenToUse: 'Invoked by the /dotnet-refactor command between its interactive gates - not standalone. Pass args {phase: "map"|"audit", lite?, repoRoot, target, services?, invariantPack?, scopeHints?, designFile?, diffCommand?}.',
  phases: [
    { title: 'Survey', detail: 'lite mode: one surveyor maps and traces a small target in one pass' },
    { title: 'Map', detail: 'one cartographer per service builds the branch/consumer/sibling map' },
    { title: 'Trace', detail: 'one tracer per path group walks the hop chain under failure conditions' },
    { title: 'Audit', detail: 'conformance auditor re-checks the branch diff against the approved design' },
  ],
}

// version: 1.8.0 (keep in sync with plugin.json; installer prints this line)
const input = typeof args === 'string' ? JSON.parse(args) : (args ?? {})
if (!input.repoRoot) return { halted: true, reason: 'args.repoRoot is required' }
const repoRoot = input.repoRoot
const target = input.target ?? '(no target given)'
const packNote = input.invariantPack
  ? `\nInvariant pack (project-specific categories and rules - classify against these):\n${input.invariantPack}`
  : ''

// ─── Schemas ───
const MAP_SCHEMA = {
  type: 'object',
  required: ['branchMap', 'consumers', 'siblings', 'docsAndLogs', 'pathGroups', 'uncertainties'],
  properties: {
    branchMap: {
      type: 'array', maxItems: 200,
      items: { type: 'object', required: ['location', 'condition', 'outcome', 'classification'], properties: { location: { type: 'string' }, condition: { type: 'string' }, outcome: { type: 'string' }, classification: { type: 'string' } } },
    },
    consumers: {
      type: 'array', maxItems: 300,
      items: { type: 'object', required: ['member', 'consumer', 'kind', 'location'], properties: { member: { type: 'string' }, consumer: { type: 'string' }, kind: { type: 'string' }, location: { type: 'string' } } },
    },
    siblings: {
      type: 'array', maxItems: 50,
      items: { type: 'object', required: ['abstraction', 'sibling', 'difference'], properties: { abstraction: { type: 'string' }, sibling: { type: 'string' }, difference: { type: 'string' } } },
    },
    docsAndLogs: {
      type: 'array', maxItems: 100,
      items: { type: 'object', required: ['artifact', 'location', 'claim'], properties: { artifact: { type: 'string' }, location: { type: 'string' }, claim: { type: 'string' } } },
    },
    pathGroups: {
      type: 'array', maxItems: 15,
      items: { type: 'object', required: ['name', 'rows'], properties: { name: { type: 'string' }, rows: { type: 'array', maxItems: 40, items: { type: 'string' } } } },
    },
    uncertainties: { type: 'array', maxItems: 40, items: { type: 'string' } },
  },
}
const TRACE_SCHEMA = {
  type: 'object',
  required: ['traces', 'anomalies', 'uncertainties'],
  properties: {
    traces: {
      type: 'array', maxItems: 20,
      items: {
        type: 'object', required: ['path', 'hops'],
        properties: {
          path: { type: 'string' },
          hops: {
            type: 'array', maxItems: 30,
            items: { type: 'object', required: ['hop', 'location', 'behavior', 'onOutage', 'onEmpty', 'onDuplicateOrMalformed'], properties: { hop: { type: 'string' }, location: { type: 'string' }, behavior: { type: 'string' }, onOutage: { type: 'string' }, onEmpty: { type: 'string' }, onDuplicateOrMalformed: { type: 'string' } } },
          },
        },
      },
    },
    anomalies: {
      type: 'array', maxItems: 60,
      items: { type: 'object', required: ['location', 'kind', 'evidence', 'consequence'], properties: { location: { type: 'string' }, kind: { enum: ['dead guard', 'conflation', 'name mismatch', 'blame direction', 'other'] }, evidence: { type: 'string' }, consequence: { type: 'string' } } },
    },
    uncertainties: { type: 'array', maxItems: 40, items: { type: 'string' } },
  },
}
const AUDIT_FINDING = {
  type: 'object', required: ['location', 'invariant', 'evidence', 'requiredChange'],
  properties: { location: { type: 'string' }, invariant: { type: 'string' }, evidence: { type: 'string' }, requiredChange: { type: 'string' } },
}
const AUDIT_SCHEMA = {
  type: 'object',
  required: ['nonconformances', 'survivors', 'staleWords', 'perInvariant', 'searchesRun'],
  properties: {
    nonconformances: { type: 'array', maxItems: 60, items: AUDIT_FINDING },
    survivors: { type: 'array', maxItems: 60, items: AUDIT_FINDING },
    staleWords: {
      type: 'array', maxItems: 60,
      items: { type: 'object', required: ['artifact', 'location', 'claim', 'shouldSay'], properties: { artifact: { type: 'string' }, location: { type: 'string' }, claim: { type: 'string' }, shouldSay: { type: 'string' } } },
    },
    perInvariant: {
      type: 'array', maxItems: 30,
      items: { type: 'object', required: ['invariant', 'verdict'], properties: { invariant: { type: 'string' }, verdict: { type: 'string' } } },
    },
    searchesRun: { type: 'array', maxItems: 40, items: { type: 'string' } },
  },
}

// ─── Phase: Audit (post design change) ───
if ((input.phase ?? 'map') === 'audit') {
  phase('Audit')
  if (!input.designFile) return { halted: true, reason: 'args.designFile is required for the audit phase' }
  const audit = await agent(
    `You are auditing a branch against its approved design. Repository root: ${repoRoot}.
Read the approved design from ${input.designFile} (the content inside the <frozen-after-approval> fence under "Design (approved)") and extract the invariants it establishes.
Produce the branch diff yourself with read-only git${input.diffCommand ? ` (the orchestrator suggests: ${input.diffCommand})` : ''}.
Follow your agent definition in full: conformance pass over every hunk, survivor sweep per eliminated defect class over the whole affected area (not just the diff), words pass, boundary-assertion check. Locations as file:line.`,
    { agentType: 'dotnet-episteme-skills:refactor:conformance-auditor', label: 'conformance-audit', schema: AUDIT_SCHEMA },
  )
  if (!audit) return { halted: true, reason: 'The conformance auditor failed to launch. Is the dotnet-episteme-skills plugin installed and enabled?' }
  return audit
}

// ─── Lite mode: one surveyor maps and traces in a single pass ───
const scopeNote = input.scopeHints ? `\nScope hints: ${input.scopeHints}` : ''
if (input.lite) {
  phase('Survey')
  const LITE_SCHEMA = {
    type: 'object',
    required: ['branchMap', 'consumers', 'siblings', 'docsAndLogs', 'traces', 'anomalies', 'uncertainties'],
    properties: {
      branchMap: MAP_SCHEMA.properties.branchMap,
      consumers: MAP_SCHEMA.properties.consumers,
      siblings: MAP_SCHEMA.properties.siblings,
      docsAndLogs: MAP_SCHEMA.properties.docsAndLogs,
      traces: TRACE_SCHEMA.properties.traces,
      anomalies: TRACE_SCHEMA.properties.anomalies,
      uncertainties: { type: 'array', maxItems: 40, items: { type: 'string' } },
      escalate: { type: 'string', description: 'set ONLY when the area is too big for lite mode: the reason; leave absent otherwise' },
    },
  }
  const survey = await agent(
    `You are the surveyor for a design loop (lighter mode: map AND trace in one pass). Repository root: ${repoRoot}. Target area: ${target}.${scopeNote}${packNote}
Follow your agent definition: build the complete map first (branch map, consumers solution-wide, siblings, docs-and-logs), then walk each path's full hop chain and evaluate the value arriving under each failure condition. Locations as file:line.
Honesty rule: if the area exceeds what one pass can trace with care (~30 branches, >3 path groups, or more than one service), stop tracing and set the escalate field with the reason instead of skimming.`,
    { agentType: 'dotnet-episteme-skills:refactor:surveyor', label: 'survey', schema: LITE_SCHEMA },
  )
  if (!survey) return { halted: true, reason: 'The surveyor failed to launch. Is the dotnet-episteme-skills plugin installed and enabled?' }
  if (survey.escalate) log(`Surveyor escalated: ${survey.escalate} - rerun with lite: false`)
  return {
    map: { branchMap: survey.branchMap, consumers: survey.consumers, siblings: survey.siblings, docsAndLogs: survey.docsAndLogs, pathGroups: [] },
    traces: survey.traces,
    anomalies: survey.anomalies,
    uncertainties: survey.uncertainties,
    lite: true,
    escalate: survey.escalate,
  }
}

// ─── Phases: Map + Trace (pipelined per service - tracing starts while other services still map) ───
// Each unit carries its own label and scope note. A single-service target is
// one unit with svc:null - never a bare null pipeline item, which the runtime
// would confuse with a failed/skipped stage result.
const serviceList = Array.isArray(input.services) && input.services.length ? input.services : []
const units = serviceList.length
  ? serviceList.map(s => ({ svc: s, label: `map:${s}`, note: `\nYour scope is the ${s} service/directory - map it completely and map nothing outside it.` }))
  : [{ svc: null, label: 'map', note: '' }]

const perService = await pipeline(
  units,
  u => agent(
    `You are the cartographer for a design loop. Repository root: ${repoRoot}. Target area: ${target}.${u.note}${scopeNote}${packNote}
Follow your agent definition's procedure: branch map, consumers solution-wide, siblings, docs-and-logs, and path groups (coherent producer-to-boundary chains for the tracers). Locations as file:line.`,
    { agentType: 'dotnet-episteme-skills:refactor:cartographer', label: u.label, phase: 'Map', schema: MAP_SCHEMA },
  ),
  async (m, u) => {
    // Stage 1 (the cartographer) returns null when the agent failed to launch;
    // the runtime also treats a null stage result as a failed/skipped item. Pass
    // it through so `perService.filter(Boolean)` and cartographersFailed handle it.
    if (!m) return null
    // Read pathGroups once, before the try, so a malformed map can never throw
    // inside the catch handler and turn a kept map into a null (misattributed
    // failed cartographer). Schema requires pathGroups, so this is belt-and-braces.
    const groups = Array.isArray(m.pathGroups) ? m.pathGroups : []
    // Preserve the map even if tracing fails - a design change still needs the
    // map, and a failed trace must not be misread as a failed cartographer.
    try {
      const traceResults = await parallel(groups.map(g => () =>
        agent(
          `You are the tracer for a design loop. Repository root: ${repoRoot}. Target area: ${target}.${packNote}
Path group: ${g.name}. Map rows in scope:
${g.rows.join('\n')}
Walk each path's full hop chain per your agent definition and evaluate the value that arrives at every hop under each failure condition (outage, rejection, empty/not-found, duplicate/integrity-break, malformed input). Hunt dead guards, conflations, name-vs-behavior mismatches, and blame-direction errors. Locations as file:line.`,
          { agentType: 'dotnet-episteme-skills:refactor:tracer', label: `trace:${g.name}`, phase: 'Trace', schema: TRACE_SCHEMA },
        ),
      ))
      return { svc: u.svc, map: m, traceResults, failedGroups: groups.filter((g, i) => !traceResults[i]).map(g => g.name), traceError: null }
    } catch (e) {
      return { svc: u.svc, map: m, traceResults: [], failedGroups: groups.map(g => g.name), traceError: String(e?.message ?? e) }
    }
  },
)

const ok = perService.filter(Boolean)
if (ok.length === 0) {
  return { halted: true, reason: 'All cartographers failed to launch. Is the dotnet-episteme-skills plugin installed and enabled?' }
}
const cartographersFailed = units.filter((u, i) => !perService[i]).map(u => u.svc ?? '(whole target)')
if (cartographersFailed.length) log(`WARNING: cartographers failed, the map is partial: ${cartographersFailed.join(', ')}`)

// A map with no path groups means nothing was traced - surface it, never let an
// untraced area read as an area traced clean.
if (ok.every(r => r.map.pathGroups.length === 0)) log('WARNING: no path groups proposed - the Trace phase ran nothing; treat failure-condition analysis as absent, not clean')
const traceErrors = ok.filter(r => r.traceError).map(r => `${r.svc ?? '(whole target)'}: ${r.traceError}`)
if (traceErrors.length) log(`WARNING: trace stage errored (map kept): ${traceErrors.join('; ')}`)

const traces = ok.flatMap(r => r.traceResults.filter(Boolean))
const result = {
  map: {
    branchMap: ok.flatMap(r => r.map.branchMap),
    consumers: ok.flatMap(r => r.map.consumers),
    siblings: ok.flatMap(r => r.map.siblings),
    docsAndLogs: ok.flatMap(r => r.map.docsAndLogs),
    pathGroups: ok.flatMap(r => r.map.pathGroups.map(g => g.name)),
  },
  traces: traces.flatMap(t => t.traces),
  anomalies: traces.flatMap(t => t.anomalies),
  uncertainties: [...ok.flatMap(r => r.map.uncertainties), ...traces.flatMap(t => t.uncertainties)],
  cartographersFailed,
  tracersFailed: ok.flatMap(r => r.failedGroups),
  tracedNothing: ok.every(r => r.map.pathGroups.length === 0),
  traceErrors,
}
log(`${result.map.branchMap.length} branches, ${result.map.consumers.length} consumers, ${result.anomalies.length} anomalies`)
return result
