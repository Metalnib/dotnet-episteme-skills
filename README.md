# DotNet Epistēmē Skills
Manual-first .NET skills repository.
## Included skills
- `dotnet-techne-inspect` — version `1.2.0`
- `dotnet-techne-code-review` — version `1.2.0`
- `dotnet-techne-crap-analysis` — version `1.0.0`
- `dotnet-techne-csharp-api-design` — version `1.0.0`
- `dotnet-techne-csharp-coding-standards` — version `1.0.0`
- `dotnet-techne-csharp-concurrency-patterns` — version `1.0.0`
- `dotnet-techne-csharp-type-design-performance` — version `1.0.0`
- `dotnet-techne-serialisation` — version `1.0.0`
## Manual install (simple)
Clone once:
```bash
git clone https://github.com/Metalnib/dotnet-episteme-skills.git ~/.local/share/dotnet-episteme-skills
```
Use these files in your AI tool (from `skills/<skill-id>/SKILL.md`).
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
