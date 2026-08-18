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
const WORKER_GROUPS = ["review", "refactor", "qa"]
const COMMANDS = ["dotnet-review", "dotnet-qa", "dotnet-refactor"]
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
  // Whole-flag --output only (git's read-only --output-indicator stays allowed).
  { action: "bash", resource: "*--output *", effect: "deny" },
  { action: "bash", resource: "*--output=*", effect: "deny" },
  // git diff --no-index is a generic file reader (any path on the machine).
  { action: "bash", resource: "*--no-index*", effect: "deny" },
  { action: "bash", resource: "* -o *", effect: "deny" },
]

// Refactor workers: same ruleset plus read-only search/list tools (fast tools
// with GNU fallbacks, probed via `command -v`). The tool allows sit before the
// operator and per-tool-flag denies so those still win (last-match-wins);
// blanket "* -o *" is dropped (rg/grep use -o for --only-matching).
const PATH_TOOLS = ["rg", "fd", "grep", "find", "ls", "eza", "cat", "head", "tail", "wc", "tree", "stat", "file"]
const SEARCH_ALLOWS = [...PATH_TOOLS, "command -v", "which"]
  .map((tool) => ({ action: "bash", resource: `${tool} *`, effect: "allow" }))
// Each tool's own write/exec flags, denied after the allows (last-match-wins).
// Coarser than the hook's regex - over-blocks some safe forms rather than ever
// under-blocking; the hook is the precise layer (docs/reviewer-restrictions.md).
const TOOL_FLAG_DENIES = [
  ...["--pre", "--hostname-bin", "--search-zip", " -z"].map((f) => ({ action: "bash", resource: `rg*${f}*`, effect: "deny" })),
  ...["-o", "--output"].map((f) => ({ action: "bash", resource: `tree*${f}*`, effect: "deny" })),
  ...["-C", "--compile"].map((f) => ({ action: "bash", resource: `file*${f}*`, effect: "deny" })),
  ...["-delete", "-exec", "-ok", "-fprint", "-fls", "--exec"].map((f) => ({ action: "bash", resource: `find*${f}*`, effect: "deny" })),
  // fd exec can cluster (fd -Hx); deny x/X 0-3 chars into a leading cluster.
  ...["x", "X"].flatMap((c) => ["-", "-?", "-??", "-???"].map((pre) => ({ action: "bash", resource: `fd* ${pre}${c}*`, effect: "deny" }))),
  ...["--exec", "--exec-batch"].map((f) => ({ action: "bash", resource: `fd*${f}*`, effect: "deny" })),
]
// Confine reads to the project (per search tool, not git): no absolute/home/parent operands.
const PATH_CONFINE_DENIES = PATH_TOOLS.flatMap((t) =>
  [`${t}* /*`, `${t}* ~/*`, `${t}* ../*`, `${t}*/../*`].map((g) => ({ action: "bash", resource: g, effect: "deny" })))
const REFACTOR_RULES = [
  ...REVIEWER_RULES.filter((r) => r.action !== "bash" || r.effect === "allow" || r.resource === "*"),
  ...SEARCH_ALLOWS,
  ...REVIEWER_RULES.filter((r) => r.action === "bash" && r.effect === "deny" && r.resource !== "*" && r.resource !== "* -o *"),
  ...TOOL_FLAG_DENIES,
  ...PATH_CONFINE_DENIES,
]
const rulesFor = (name) => (name.startsWith("refactor-") ? REFACTOR_RULES : REVIEWER_RULES)

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

async function loadWorkers() {
  const groups = await Promise.all(
    WORKER_GROUPS.map(async (group) => {
      const dir = path.join(ROOT, "agents", group)
      const files = (await readdir(dir)).filter((f) => f.endsWith(".md")).sort()
      return Promise.all(
        files.map(async (file) => {
          const { description, body } = split(await readFile(path.join(dir, file), "utf8"))
          return { name: `${group}-${path.basename(file, ".md")}`, description, prompt: body }
        }),
      )
    }),
  )
  return groups.flat()
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
    const missingPaths = [
      ...WORKER_GROUPS.map((g) => path.join(ROOT, "agents", g)),
      ...COMMANDS.map((c) => path.join(ROOT, "opencode", `${c}.template.md`)),
    ].filter((p) => !existsSync(p))
    if (missingPaths.length) {
      console.error(
        `[dotnet-episteme v2] plugin files missing at ${ROOT} (${missingPaths.map((p) => path.relative(ROOT, p)).join(", ")}) - ` +
          `this file must be a symlink (or shim) into a complete cloned repo. Re-run scripts/install-opencode.sh --v2.`,
      )
      return
    }
    const workers = await loadWorkers()
    const templates = Object.fromEntries(
      await Promise.all(
        COMMANDS.map(async (name) => [name, split(await readFile(path.join(ROOT, "opencode", `${name}.template.md`), "utf8"))]),
      ),
    )

    transform(ctx, "agent", (agents) => {
      for (const worker of workers) {
        agents.update(worker.name, (agent) => {
          agent.description ??= worker.description
          agent.mode ??= "subagent"
          agent.system ??= worker.prompt
          agent.permissions ??= []
          if (agent.permissions.length === 0) agent.permissions.push(...rulesFor(worker.name))
        })
      }
      return agents
    })

    transform(ctx, "command", (commands) => {
      for (const [name, template] of Object.entries(templates)) {
        commands.update(name, (command) => {
          command.description ??= template.description
          command.template ??= template.body
            .replace(/<!--[\s\S]*?-->\s*/g, "")
            .replaceAll("{{PLUGIN_ROOT}}", ROOT)
            .replaceAll(
              "{{TIER_GUIDANCE}}",
              "Every lane runs on the current session model (v2: register pinned variants when the API settles).",
            )
        })
      }
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
