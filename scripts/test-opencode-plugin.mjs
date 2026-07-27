#!/usr/bin/env node
// Exercises opencode/dotnet-episteme.js against a fake config object, so CI
// catches a broken registration without an OpenCode install. Run: node scripts/test-opencode-plugin.mjs
import { accessSync, constants } from "node:fs"
import path from "node:path"
import { fileURLToPath } from "node:url"

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")
const LANES = [
  "review-correctness",
  "review-performance",
  "review-security-observability",
  "review-data-messaging",
  "review-generalist",
  "review-maintainer",
]

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
check("six review subagents registered", missing.length === 0, `missing ${missing}`)

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

const command = config.command?.["dotnet-review"]
check("/dotnet-review command registered", !!command)
if (command) {
  check("command template fully substituted", !command.template.includes("{{"), "placeholder left in template")
  check("contributor comments stripped", !command.template.includes("<!--"))
  check("command template resolves plugin paths", command.template.includes(path.join(ROOT, "skills")))
  check("command carries a description", (command.description ?? "").length > 20)
  check(
    "command names the OpenCode subagents",
    LANES.every((lane) => command.template.includes(lane)),
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

// A user's own definitions must survive plugin registration.
const preset = { agent: { "review-correctness": { description: "mine" } }, command: {}, mcp: {} }
await (await DotnetEpisteme({ directory: ROOT }, undefined)).config(preset)
check("existing user definitions are not clobbered", preset.agent["review-correctness"].description === "mine")

console.log(failures ? `\n${failures} check(s) failed` : "\nOpenCode plugin registration OK")
process.exit(failures ? 1 : 0)
