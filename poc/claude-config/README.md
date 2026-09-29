# Claude Code configuration (install once)

These files belong in the repo's `.claude/` folder, where Claude Code reads them. They're kept here because they were generated outside Claude Code.

| Source | Destination |
|---|---|
| `agents/*.md` | `.claude/agents/` (project subagents) |
| `settings.json` | `.claude/settings.json` (shared permissions allowlist + subagent nesting limit) |

**Install** (from the repo root, in PowerShell):

```powershell
New-Item -ItemType Directory -Force .claude\agents | Out-Null
Copy-Item poc\claude-config\agents\*.md .claude\agents\ -Force
Copy-Item poc\claude-config\settings.json .claude\settings.json   # skip if you already have one; merge by hand instead
```

Or ask Claude Code: *"Install poc/claude-config into .claude as described in its README."*

Then **restart Claude Code** and run `/agents` to confirm the six agents are listed (files added mid-session may not load until a restart).

After installing, `.claude/` is the source of truth. Edit agents there (or with the `/agents` command) and commit `.claude/`. You can delete this folder afterwards, or keep it as a reference.
