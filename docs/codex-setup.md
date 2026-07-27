# Using these skills in Codex

Needs Codex CLI 0.145.0 or newer.

## Install

```bash
codex plugin marketplace add Metalnib/dotnet-episteme-skills
codex plugin add dotnet-episteme-skills@dotnet-episteme-marketplace
scripts/install-codex.sh
```

The first two commands add the skills, the Synopsis graph tools, and a guard that keeps reviewers from changing files. The script adds the six reviewer roles, which a plugin is not allowed to install.

No clone? Run the script from the installed copy:

```bash
~/.codex/plugins/cache/dotnet-episteme-marketplace/dotnet-episteme-skills/*/scripts/install-codex.sh
```

You can re-run it any time. `--verify` checks the install, `--uninstall` removes it.

On first start Codex says `1 hook needs review before it can run`. Choose **Trust all and continue**. Codex remembers that answer, but it does not re-check the script when you update it, so read `hooks/git-readonly-guard.sh` yourself after an update.

## Using it

- **The 10 skills**: type `$` and the skill name (or run `/skills`), or just describe what you need — Codex picks the right one.
- **A full review**: ask for a multi-agent review of a branch, a commit range, or your current changes. Codex runs five reviewers in parallel, then a sixth that tries to disprove their findings, and reports what survived.
- **Dependency questions** ("what breaks if I change this?"): ask, and Codex uses the Synopsis graph tools.

There is no `/dotnet-review` command in Codex — it does not support custom commands, so asking in your own words is the way in.

## A full review is not cheap

Each review uses **six agents**. On a metered or free plan that adds up fast: one review of a two-commit range can use most of a small monthly allowance. For a small change, ask for a normal code review instead — that runs in one pass.

The install script also raises Codex's limit on parallel agents to 6. Without that the five reviewers run three at a time, which is slower but still correct.

## Checking the install

```bash
scripts/install-codex.sh --verify   # roles and config
codex plugin list                  # should say: installed, enabled
codex mcp get synopsis             # the graph server
```

Inside Codex, `/skills` lists the skills and `/hooks` should show `PreToolUse  Installed 1  Active 1`.

## Good to know

- **Windows**: the graph server starts through a shell script, so run Codex under WSL2 to get it. Skills and reviewers work on Windows as they are.
- Reviewers cannot edit files (read-only sandbox), and the plugin's guard hook limits their shell to read-only `git` inside your project. The full contract: [reviewer-restrictions.md](reviewer-restrictions.md).
- The experimental log monitor works in Claude Code only.

## If something goes wrong

| What you see | What to do |
|---|---|
| `MCP startup failed: No such file or directory` | An old install is registered. Run `codex plugin remove …` then `codex plugin add …` again. |
| No Synopsis tools | `codex mcp get synopsis`. The first run downloads a binary — check `~/.synopsis/synopsis.log`. |
| Codex cannot find the reviewer roles | `scripts/install-codex.sh --verify` |
| Reviewers run three at a time | Your config caps parallel agents below 6. |
| Every `codex` command fails after you edited `config.toml` | The file has a syntax error — most often the same `[agents]` heading twice. |
