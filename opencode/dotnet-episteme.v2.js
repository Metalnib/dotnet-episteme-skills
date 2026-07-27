/**
 * EXPERIMENTAL OpenCode v2 plugin (dormant): the v2 plugin API is beta and no
 * shipping OpenCode runs it yet - linked only via `install-opencode.sh --v2`.
 *
 * Deliberately self-contained: the v1 loader scans every export of a plugin
 * module, and a v2-style default export in `dotnet-episteme.js` silently
 * breaks its v1 registration (verified on 1.18.7) - so nothing is shared.
 */
import { existsSync } from "node:fs"
import { readdir, readFile } from "node:fs/promises"
import path from "node:path"
import { fileURLToPath } from "node:url"

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")
const REVIEW_AGENTS = path.join(ROOT, "agents", "review")
const COMMAND_TEMPLATE = path.join(ROOT, "opencode", "dotnet-review.template.md")
const MCP_LAUNCHER = path.join(ROOT, "bin", "synopsis-mcp-launcher.sh")

// v2 permissions are an ordered ruleset; resolution is last-match-wins, so the
// operator/output denies sit after the git allows (same table as v1).
const REVIEWER_RULES = [
  { action: "edit", resource: "*", effect: "deny" },
  { action: "webfetch", resource: "*", effect: "deny" },
  { action: "websearch", resource: "*", effect: "deny" },
  { action: "task", resource: "*", effect: "deny" },
  { action: "external_directory", resource: "*", effect: "deny" },
  { action: "external_directory", resource: `${ROOT}/*`, effect: "allow" },
  { action: "bash", resource: "*", effect: "deny" },
  { action: "bash", resource: "git diff*", effect: "allow" },
  { action: "bash", resource: "git log*", effect: "allow" },
  { action: "bash", resource: "git show*", effect: "allow" },
  { action: "bash", resource: "git blame*", effect: "allow" },
  { action: "bash", resource: "git status*", effect: "allow" },
  { action: "bash", resource: "git rev-parse*", effect: "allow" },
  { action: "bash", resource: "git merge-base*", effect: "allow" },
  { action: "bash", resource: "*;*", effect: "deny" },
  { action: "bash", resource: "*|*", effect: "deny" },
  { action: "bash", resource: "*&*", effect: "deny" },
  { action: "bash", resource: "*`*", effect: "deny" },
  { action: "bash", resource: "*$(*", effect: "deny" },
  { action: "bash", resource: "*>*", effect: "deny" },
  { action: "bash", resource: "*<*", effect: "deny" },
  { action: "bash", resource: "*\n*", effect: "deny" },
  { action: "bash", resource: "*--output*", effect: "deny" },
  { action: "bash", resource: "* -o *", effect: "deny" },
]

function split(text) {
  const lines = text.split("\n")
  if (lines[0]?.trim() !== "---") return { description: "", body: text }
  const end = lines.findIndex((line, i) => i > 0 && line.trim() === "---")
  if (end < 0) return { description: "", body: text }
  const raw = lines.slice(1, end).find((line) => line.startsWith("description:")) ?? ""
  return {
    description: raw.slice("description:".length).trim().replace(/^"|"$/g, ""),
    body: lines.slice(end + 1).join("\n").trim(),
  }
}

async function loadReviewers() {
  const files = (await readdir(REVIEW_AGENTS)).filter((f) => f.endsWith(".md")).sort()
  return Promise.all(
    files.map(async (file) => {
      const { description, body } = split(await readFile(path.join(REVIEW_AGENTS, file), "utf8"))
      return { name: `review-${path.basename(file, ".md")}`, description, prompt: body }
    }),
  )
}

/** Apply a draft transform defensively - beta draft shapes may change. */
function transform(ctx, kind, fn) {
  const hook = ctx?.[kind]?.transform
  if (typeof hook !== "function") {
    console.warn(`[dotnet-episteme v2] ctx.${kind}.transform unavailable - ${kind} not registered (beta API drift?)`)
    return
  }
  hook.call(ctx[kind], fn)
}

export default {
  id: "metalnib.dotnet-episteme",
  server: async (ctx) => {
    if (!existsSync(REVIEW_AGENTS)) {
      console.error(
        `[dotnet-episteme v2] repo files not found at ${ROOT} - this file must be a ` +
          `symlink (or shim) into the cloned repo. Re-run scripts/install-opencode.sh --v2.`,
      )
      return
    }
    const reviewers = await loadReviewers()
    const template = split(await readFile(COMMAND_TEMPLATE, "utf8"))

    transform(ctx, "agent", (agents) => {
      for (const reviewer of reviewers) {
        agents.update(reviewer.name, (agent) => {
          agent.description ??= reviewer.description
          agent.mode ??= "subagent"
          agent.system ??= reviewer.prompt
          agent.permissions ??= []
          if (agent.permissions.length === 0) agent.permissions.push(...REVIEWER_RULES)
        })
      }
      return agents
    })

    transform(ctx, "command", (commands) => {
      commands.update("dotnet-review", (command) => {
        command.description ??= template.description
        command.template ??= template.body
          .replace(/<!--[\s\S]*?-->\s*/g, "")
          .replaceAll("{{PLUGIN_ROOT}}", ROOT)
          .replaceAll(
            "{{TIER_GUIDANCE}}",
            "Every reviewer runs on the current session model (v2: register pinned variants when the API settles).",
          )
      })
      return commands
    })

    transform(ctx, "skill", (skills) => {
      skills.update?.(path.join(ROOT, "skills"), () => {})
      return skills
    })

    // No MCP transform exists in the beta API yet; Synopsis must be added to
    // the config until one lands.
    if (process.platform !== "win32" && !ctx?.mcp) {
      console.warn(
        `[dotnet-episteme v2] register Synopsis manually until v2 exposes MCP registration: ` +
          `mcp.servers.synopsis = { type: "local", command: ["${MCP_LAUNCHER}"] }`,
      )
    }
  },
}
