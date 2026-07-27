/**
 * OpenCode plugin: registers the skills, the review subagents, `/dotnet-review`
 * and the Synopsis MCP server. Install via `scripts/install-opencode.sh`.
 *
 * Reviewer prompts are read from `agents/review/*.md` so those files stay the
 * single source across tools; their Claude frontmatter is dropped because a
 * comma-string `tools:` key makes OpenCode reject the whole config document.
 */
import { readdir, readFile } from "node:fs/promises"
import path from "node:path"
import { fileURLToPath } from "node:url"

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")
const REVIEW_AGENTS = path.join(ROOT, "agents", "review")
const COMMAND_TEMPLATE = path.join(ROOT, "opencode", "dotnet-review.template.md")
const MCP_LAUNCHER = path.join(ROOT, "bin", "synopsis-mcp-launcher.sh")

/**
 * Reviewers get read-only git and nothing else. Patterns anchor on the
 * subcommand so `git -C <elsewhere> diff` matches no allow rule.
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
        // Template comments are for contributors, not the model.
        template: template.body
          .replace(/<!--[\s\S]*?-->\s*/g, "")
          .replaceAll("{{PLUGIN_ROOT}}", ROOT)
          .replaceAll("{{TIER_GUIDANCE}}", tierGuidance(strong)),
      }

      // The launcher needs a POSIX shell, which native Windows lacks.
      if (process.platform !== "win32") {
        config.mcp ??= {}
        config.mcp["synopsis"] ??= { type: "local", command: [MCP_LAUNCHER], enabled: true }
      }
    },
  }
}
