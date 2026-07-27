/**
 * OpenCode plugin for dotnet-episteme-skills.
 *
 * Registers, from this checkout, everything the Claude Code plugin registers
 * through `.claude-plugin/plugin.json` + `.mcp.json`:
 *   - the 10 `dotnet-techne-*` skills (as a skills path)
 *   - the five review subagents + the adversarial maintainer
 *   - the `/dotnet-review` orchestrator command
 *   - the Synopsis MCP server
 *
 * The reviewer prompts are read from `agents/review/*.md` at load time, so
 * those files stay the single source of truth for both tools. Their Claude
 * frontmatter is discarded here: `tools: Read, Grep, …` is a comma string
 * where OpenCode's schema wants a map, and OpenCode rejects the whole config
 * document over it. Never copy those files into an OpenCode agent directory.
 *
 * Install: symlink this file into `~/.config/opencode/plugin/` (see
 * `scripts/install-opencode.sh`) or install the published npm package.
 */
import { readdir, readFile } from "node:fs/promises"
import path from "node:path"
import { fileURLToPath } from "node:url"

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")
const REVIEW_AGENTS = path.join(ROOT, "agents", "review")
const COMMAND_TEMPLATE = path.join(ROOT, "opencode", "dotnet-review.template.md")
const MCP_LAUNCHER = path.join(ROOT, "bin", "synopsis-mcp-launcher.sh")

/**
 * Mirrors `hooks/git-readonly-guard.sh`. OpenCode evaluates these last-match-wins
 * and parses chained commands, so `git status && rm -rf x` is denied by the `*`
 * rule. Patterns anchor on the subcommand, so `git -C /elsewhere diff` never
 * matches an allow — which is stricter than the Claude hook's `-C` path check.
 */
const REVIEWER_PERMISSION = {
  edit: "deny",
  webfetch: "deny",
  bash: {
    "*": "deny",
    "git diff*": "allow",
    "git log*": "allow",
    "git show*": "allow",
    "git blame*": "allow",
    "git status*": "allow",
    "git rev-parse*": "allow",
    "git merge-base*": "allow",
  },
}

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

async function loadReviewers() {
  const files = (await readdir(REVIEW_AGENTS)).filter((f) => f.endsWith(".md")).sort()
  return Promise.all(
    files.map(async (file) => {
      const { description, body } = split(await readFile(path.join(REVIEW_AGENTS, file), "utf8"))
      return { name: `review-${path.basename(file, ".md")}`, description, prompt: body }
    }),
  )
}

/**
 * A strong tier is optional because OpenCode's `task` tool has no per-call
 * model parameter: the only way to give a lane a stronger model is a second
 * agent that pins one. Set it via plugin options `{ strongModel }` or
 * `DOTNET_EPISTEME_STRONG_MODEL` (options only reach config-declared plugins).
 */
function strongModel(options) {
  const value = options?.strongModel ?? process.env.DOTNET_EPISTEME_STRONG_MODEL
  return typeof value === "string" && value.trim() ? value.trim() : undefined
}

function tierGuidance(strong) {
  if (strong)
    return (
      `Strong-tier variants are registered: every lane also exists as ` +
      `\`review-<lane>-strong\`, pinned to \`${strong}\`. When the sizing table calls for a ` +
      `stronger tier, dispatch the \`-strong\` variants instead of the plain ones (the ` +
      `maintainer is never weaker than the reviewers, so promote it together with them). ` +
      `Otherwise use the plain names, which inherit the session model.`
    )
  return (
    "OpenCode's `task` tool has no per-invocation model parameter, so every reviewer runs on " +
    "the **current session model**. If that model is weaker than the tier the change deserves, " +
    "say so in one line before dispatching and let the user switch model and re-run rather than " +
    "silently under-reviewing. (Registering a stronger tier: see docs/opencode-setup.md.)"
  )
}

export const DotnetEpisteme = async (_input, options) => {
  const reviewers = await loadReviewers()
  const template = split(await readFile(COMMAND_TEMPLATE, "utf8"))
  const strong = strongModel(options)

  return {
    config: async (config) => {
      // OpenCode 1.18.x hands the plugin the v1-shaped config and ignores v2
      // containers written here. Warn instead of silently registering nothing.
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
      for (const reviewer of reviewers) {
        config.agent[reviewer.name] ??= {
          description: reviewer.description,
          mode: "subagent",
          prompt: reviewer.prompt,
          permission: REVIEWER_PERMISSION,
        }
        if (strong) {
          config.agent[`${reviewer.name}-strong`] ??= {
            description: `${reviewer.description} (strong tier: ${strong})`,
            mode: "subagent",
            model: strong,
            prompt: reviewer.prompt,
            permission: REVIEWER_PERMISSION,
          }
        }
      }

      config.command ??= {}
      config.command["dotnet-review"] ??= {
        description: template.description,
        // Contributor notes in the template are for humans, not for the model.
        template: template.body
          .replace(/<!--[\s\S]*?-->\s*/g, "")
          .replaceAll("{{PLUGIN_ROOT}}", ROOT)
          .replaceAll("{{TIER_GUIDANCE}}", tierGuidance(strong)),
      }

      // The launcher is a POSIX shell script: on native Windows there is no
      // interpreter for it, so Synopsis stays CLI-only there (WSL2 reports linux).
      if (process.platform !== "win32") {
        config.mcp ??= {}
        config.mcp["synopsis"] ??= { type: "local", command: [MCP_LAUNCHER], enabled: true }
      }
    },
  }
}
