# DotNet Epistēmē Skills
Production-tested .NET skills repository for AI coding tools.
These skills provide practical, real-world, enterprise-proven instructions that help AI agents implement, review, and improve .NET systems with production readiness in mind.

## What kind of skills are provided?
### Code review and risk analysis
- `dotnet-techne-code-review` (`1.2.0`) — end-to-end .NET review: correctness, performance, security, data, messaging, observability.
- `dotnet-techne-crap-analysis` (`1.0.0`) — CRAP score and coverage hotspot analysis.

### API and coding design
- `dotnet-techne-csharp-api-design` (`1.0.0`) — compatibility-safe API evolution and versioning.
- `dotnet-techne-csharp-coding-standards` (`1.0.0`) — modern C# coding standards and refactoring guidance.

### Performance and concurrency
- `dotnet-techne-csharp-concurrency-patterns` (`1.0.0`) — choosing async/channels/dataflow/Rx patterns.
- `dotnet-techne-csharp-type-design-performance` (`1.0.0`) — readonly structs, sealed types, spans, frozen collections.
- `dotnet-techne-inspect` (`1.2.0`) — inspect NuGet package APIs and decompile signatures.

### Serialisation and contracts
- `dotnet-techne-serialisation` (`1.0.0`) — serialisation format and wire compatibility decisions.

## Repository layout
- Each skill is in `skills/<skill-id>/`.
- Every skill has a `SKILL.md` entrypoint.
- Some skills include companion docs and scripts; keep the whole folder together when installing manually.

## Manual installation (AI tool of your choice)
### 1) Clone this repository
```bash
git clone https://github.com/Metalnib/dotnet-episteme-skills.git ~/.local/share/dotnet-episteme-skills
```

### 2) Install into your tool's skill directory
You can copy all skills or only selected skill folders.

### Claude Code
Claude Code loads skills from:
- Global: `~/.claude/skills`
- Project: `.claude/skills`

Install all skills globally:
```bash
mkdir -p ~/.claude/skills
cp -R ~/.local/share/dotnet-episteme-skills/skills/* ~/.claude/skills/
```

Install one skill only:
```bash
mkdir -p ~/.claude/skills
cp -R ~/.local/share/dotnet-episteme-skills/skills/dotnet-techne-code-review ~/.claude/skills/
```

### OpenCode
OpenCode loads skills from:
- Global: `~/.config/opencode/skill`
- Project: `.opencode/skill`

Install all skills globally:
```bash
mkdir -p ~/.config/opencode/skill
cp -R ~/.local/share/dotnet-episteme-skills/skills/* ~/.config/opencode/skill/
```

### Other Agent Skills-compatible tools
If your tool supports Agent Skills format:
- copy any skill folder that contains `SKILL.md`
- keep companion files in the same folder
- point your tool to that skills directory according to its documentation

## Invocation behavior in this repo
Each skill is configured to be invocable by both model and user:
- `disable-model-invocation: false`
- `user-invocable: true`

`metadata.trigger_keywords` is included as extra routing context. Tools that do not support it can safely ignore it.

## Phase 2: CI publishing
Phase 2 adds tag-driven publishing inspired by `aaron-dotnet-skills`:
- validates plugin and skill registry
- validates marketplace metadata alignment
- creates a GitHub release with a packaged archive

### Release workflow trigger
- workflow file: `.github/workflows/release.yml`
- trigger: push tag `v*` (example: `v1.2.0`)

### Before releasing
1. Update version in `.claude-plugin/plugin.json`
2. Set matching version in `.claude-plugin/marketplace.json` (`plugins[0].version`)
3. Run local validation:
```bash
bash scripts/validate.sh
bash scripts/validate-marketplace.sh
```

### Publish a release
```bash
git tag v1.2.0
git push origin v1.2.0
```

## Update
```bash
git -C ~/.local/share/dotnet-episteme-skills pull
```

If you used copy-based install, copy updated skill folders again to your tool directory.

## Validate repository
```bash
bash scripts/validate.sh
bash scripts/validate-marketplace.sh
```

## Specification and docs references
- Agent Skills specification: https://agentskills.io/specification
- Claude Code skills docs: https://code.claude.com/docs/en/skills
- OpenCode skills docs: https://opencode.ai/docs/skills

## License
MIT
