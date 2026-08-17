/**
 * OpenCode plugin: registers the skills, the review/refactor/qa subagents,
 * `/dotnet-review`, `/dotnet-qa`, `/dotnet-refactor` and the Synopsis MCP
 * server. Install via `scripts/install-opencode.sh`.
 *
 * Worker prompts are read from `agents/{review,refactor,qa}/*.md` so those
 * files stay the single source across tools; their Claude frontmatter is
 * dropped because a comma-string `tools:` key makes OpenCode reject the whole
 * config document.
 */
import { existsSync } from "node:fs"
import { readdir, readFile } from "node:fs/promises"
import path from "node:path"
import { fileURLToPath } from "node:url"

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")
const WORKER_GROUPS = ["review", "refactor", "qa"]
const COMMANDS = ["dotnet-review", "dotnet-qa", "dotnet-refactor"]
const MCP_LAUNCHER = path.join(ROOT, "bin", "synopsis-mcp-launcher.sh")

/**
 * One posture across tools: docs/reviewer-restrictions.md. Resolution is
 * last-match-wins, so the operator/output denies sit AFTER the allows -
 * `git diff && curl x` matches `git diff*` but the later `*&*` deny wins.
 */
const GIT_ALLOWS = {
  "git diff*": "allow",
  "git log*": "allow",
  "git show*": "allow",
  "git blame*": "allow",
  "git status*": "allow",
  "git rev-parse*": "allow",
  "git merge-base*": "allow",
}
const OPERATOR_DENIES = {
  "*;*": "deny",
  "*|*": "deny",
  "*&*": "deny",
  "*`*": "deny",
  "*$(*": "deny",
  "*>*": "deny",
  "*<*": "deny",
  "*\n*": "deny",
  // Whole-flag --output only, so git's read-only --output-indicator-{new,old,context}
  // stay allowed - matching the hook (docs/reviewer-restrictions.md: one posture).
  // `--output *` also catches a trailing bare `--output` via Wildcard's " .*" rule.
  "*--output *": "deny",
  "*--output=*": "deny",
  // git diff --no-index is a generic file reader (any path on the machine).
  "*--no-index*": "deny",
}
const REVIEWER_PERMISSION = {
  edit: "deny",
  webfetch: "deny",
  // websearch is a separate permission from webfetch; both are exfil channels.
  websearch: "deny",
  // task would let a worker spawn a nested agent WITHOUT these restrictions.
  task: "deny",
  // Checklists live in the plugin repo, outside the reviewed project - the
  // default `ask` would stall a headless subagent.
  external_directory: { "*": "deny", [`${ROOT}/*`]: "allow" },
  bash: { "*": "deny", ...GIT_ALLOWS, ...OPERATOR_DENIES, "* -o *": "deny" },
}
// Refactor workers sweep whole solutions: same posture plus read-only
// search/list tools - fast tools (rg, fd) with GNU fallbacks, probed via
// `command -v`. No blanket "* -o *" deny: rg/grep use -o for --only-matching
// (safe). Instead each tool's own write/exec flags are denied after the allows
// (last-match-wins): rg --pre/--hostname-bin/--search-zip/-z run subprocesses,
// tree -o and file -C write files, find/fd exec/delete flags run commands.
const PATH_TOOLS = ["rg", "fd", "grep", "find", "ls", "eza", "cat", "head", "tail", "wc", "tree", "stat", "file"]
const SEARCH_ALLOWS = Object.fromEntries(
  [...PATH_TOOLS, "command -v", "which"].map((tool) => [`${tool} *`, "allow"]),
)
// glob enforcement is coarser than the hook's regex - it over-blocks some safe
// clustered forms and quoted patterns rather than ever under-blocking; the hook
// is the precise layer (docs/reviewer-restrictions.md).
const TOOL_FLAG_DENIES = {
  ...Object.fromEntries(["--pre", "--hostname-bin", "--search-zip", " -z"].map((f) => [`rg*${f}*`, "deny"])),
  ...Object.fromEntries(["-o", "--output"].map((f) => [`tree*${f}*`, "deny"])),
  ...Object.fromEntries(["-C", "--compile"].map((f) => [`file*${f}*`, "deny"])),
  ...Object.fromEntries(["-delete", "-exec", "-ok", "-fprint", "-fls", "--exec"].map((f) => [`find*${f}*`, "deny"])),
  // fd exec: -x/-X can cluster after other short flags (fd -Hx), which a plain
  // `-x` substring glob misses - deny x/X 0-3 chars into a leading cluster.
  ...Object.fromEntries(
    ["x", "X"].flatMap((c) => ["-", "-?", "-??", "-???"].map((pre) => [`fd* ${pre}${c}*`, "deny"])).concat(
      ["--exec", "--exec-batch"].map((f) => [`fd*${f}*`, "deny"]),
    ),
  ),
}
// Confine reads to the project: no absolute/home/parent paths as operands.
// Per search tool (not git, whose -C confinement is the hook's job), so a
// reviewer's `git -C <abs project>` is unaffected. Coarser than the hook: it
// also blocks quoted patterns containing " /" - use relative paths or Grep.
const PATH_CONFINE_DENIES = Object.fromEntries(
  PATH_TOOLS.flatMap((t) => [`${t}* /*`, `${t}* ~/*`, `${t}* ../*`, `${t}*/../*`].map((g) => [g, "deny"])),
)
const REFACTOR_PERMISSION = {
  ...REVIEWER_PERMISSION,
  bash: { "*": "deny", ...GIT_ALLOWS, ...SEARCH_ALLOWS, ...OPERATOR_DENIES, ...TOOL_FLAG_DENIES, ...PATH_CONFINE_DENIES },
}
const permissionFor = (name) => (name.startsWith("refactor-") ? REFACTOR_PERMISSION : REVIEWER_PERMISSION)

/** Splits YAML frontmatter from a markdown body without a YAML dependency. */
function split(text) {
  const lines = text.split("\n")
  if (lines[0]?.trim() !== "---") return { description: "", body: text }
  const end = lines.findIndex((line, i) => i > 0 && line.trim() === "---")
  if (end < 0) return { description: "", body: text }
  const front = lines.slice(1, end)
  const raw = front.find((line) => line.startsWith("description:")) ?? ""
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

/**
 * `task` takes no per-call model, so a stronger tier needs a second agent that
 * pins one. Options only reach config-declared plugins, hence the env var too.
 */
function strongModel(options) {
  const value = options?.strongModel ?? process.env.DOTNET_EPISTEME_STRONG_MODEL
  return typeof value === "string" && value.trim() ? value.trim() : undefined
}

function tierGuidance(strong) {
  if (strong)
    return (
      `Strong-tier variants are registered: every lane also exists as ` +
      `\`<lane>-strong\`, pinned to \`${strong}\`. When the sizing table calls for a ` +
      `stronger tier, dispatch the \`-strong\` variants instead of the plain ones (the ` +
      `maintainer is never weaker than the other lanes, so promote it together with them). ` +
      `Otherwise use the plain names, which inherit the session model.`
    )
  return (
    "OpenCode's `task` tool has no per-invocation model parameter, so every lane runs on " +
    "the **current session model**. If that model is weaker than the tier the change deserves, " +
    "say so in one line before dispatching and let the user switch model and re-run rather than " +
    "silently under-reviewing. (Registering a stronger tier: see docs/opencode-setup.md.)"
  )
}

export const DotnetEpisteme = async (_input, options) => {
  // A COPIED plugin file resolves ROOT to the config dir, not the repo, and a
  // partial checkout can leave some worker dirs or templates missing. Check
  // every path the plugin is about to read and fail with the missing list
  // instead of letting readFile/readdir throw and kill every registration.
  const needed = [
    ...WORKER_GROUPS.map((g) => path.join(ROOT, "agents", g)),
    ...COMMANDS.map((c) => path.join(ROOT, "opencode", `${c}.template.md`)),
  ]
  const missingPaths = needed.filter((p) => !existsSync(p))
  if (missingPaths.length) {
    console.error(
      `[dotnet-episteme] plugin files missing at ${ROOT} (${missingPaths.map((p) => path.relative(ROOT, p)).join(", ")}) - ` +
        `this file must be a symlink (or shim) into a complete cloned repo, not a copy or partial checkout. ` +
        `Re-run scripts/install-opencode.sh (or .ps1).`,
    )
    return {}
  }
  const workers = await loadWorkers()
  const templates = Object.fromEntries(
    await Promise.all(
      COMMANDS.map(async (name) => [name, split(await readFile(path.join(ROOT, "opencode", `${name}.template.md`), "utf8"))]),
    ),
  )
  const strong = strongModel(options)

  return {
    config: async (config) => {
      // 1.18.x hands us the v1-shaped config; a v2 one would register nothing.
      if (!config.agent && (config.agents || config.commands || Array.isArray(config.skills))) {
        console.warn(
          "[dotnet-episteme] OpenCode passed a v2-shaped config; this plugin version writes v1 keys. Update the plugin.",
        )
      }

      config.skills ??= {}
      config.skills.paths ??= []
      const skills = path.join(ROOT, "skills")
      if (!config.skills.paths.includes(skills)) config.skills.paths.push(skills)

      config.agent ??= {}
      for (const worker of workers) {
        config.agent[worker.name] ??= {
          description: worker.description,
          mode: "subagent",
          prompt: worker.prompt,
          permission: permissionFor(worker.name),
        }
        if (strong) {
          config.agent[`${worker.name}-strong`] ??= {
            description: `${worker.description} (strong tier: ${strong})`,
            mode: "subagent",
            model: strong,
            prompt: worker.prompt,
            permission: permissionFor(worker.name),
          }
        }
      }

      config.command ??= {}
      for (const [name, template] of Object.entries(templates)) {
        config.command[name] ??= {
          description: template.description,
          // Template comments are for contributors, not the model.
          template: template.body
            .replace(/<!--[\s\S]*?-->\s*/g, "")
            .replaceAll("{{PLUGIN_ROOT}}", ROOT)
            .replaceAll("{{TIER_GUIDANCE}}", tierGuidance(strong)),
        }
      }

      // The launcher needs a POSIX shell, which native Windows lacks.
      if (process.platform !== "win32") {
        config.mcp ??= {}
        config.mcp["synopsis"] ??= { type: "local", command: [MCP_LAUNCHER], enabled: true }
      }
    },
  }
}
