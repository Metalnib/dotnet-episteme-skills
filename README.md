# DotNet Epistēmē Skills
Manual-first .NET skills repository.
## Included skills
- `dotnet-techne-inspect` (source: local `dotnet-inspect`)
- `dotnet-techne-code-review` (source: local `code-review`)
## Manual install (simple)
Clone once:
```bash
git clone https://github.com/Metalnib/dotnet-episteme-skills.git ~/.local/share/dotnet-episteme-skills
```
Use these files in your AI tool:
- `skills/dotnet-techne-inspect/SKILL.md`
- `skills/dotnet-techne-code-review/SKILL.md`
If your tool supports helper scripts, keep each skill's `scripts/` directory together with its `SKILL.md`.
## Update
```bash
git -C ~/.local/share/dotnet-episteme-skills pull
```
## Validation
```bash
bash scripts/validate.sh
```
## License
MIT
