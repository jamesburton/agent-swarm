---
created: 2026-10-03
updated: 2026-10-03
status: current
---

# dnx invocation notes

How to run the `swarm` tool through `dnx` (.NET 10 SDK 10.0.401, Windows 11). Every row was run against a real `dotnet pack` package served from a local folder feed, with stdin closed and no prompt ever appearing. `FEED` below stands for the folder holding `Swarm.Cli.0.1.0.nupkg`; `PKG` for the package id (currently the placeholder `Swarm.Cli`). Git Bash needs `dnx.cmd`; PowerShell resolves plain `dnx` to the same `dnx.cmd`.

Pack the feed: `cd src/Swarm.Cli && dotnet pack -c Release -o FEED`.

## Working invocations (copy-paste)

```bash
# Git Bash
dnx.cmd Swarm.Cli@0.1.0 --add-source FEED -- validate path/to/definition.md
dnx.cmd Swarm.Cli@0.1.0 --add-source FEED -- render path/to/definition.md --out path/to/project
```

```powershell
# PowerShell
dnx Swarm.Cli@0.1.0 --add-source FEED -- validate path/to/definition.md
```

## Verified facts

| # | Fact | Command | Status |
|---|------|---------|--------|
| 1 | `--add-source` with a local folder feed resolves the package, with the `@version` form or `--version` | `dnx Swarm.Cli@0.1.0 --add-source FEED -- validate f.md`; `dnx Swarm.Cli --version 0.1.0 --add-source FEED -- validate f.md` (exit 0, stdout `ok`) | verified |
| 2 | The `--` separator is optional for tool arguments that dnx does not define; both forms reach the tool | `... --add-source FEED validate f.md` and `... -- validate f.md` both print `ok` | verified |
| 3 | After `--`, arguments pass to the tool untouched, even ones dnx also defines | `dnx Swarm.Cli@0.1.0 --add-source FEED -- --version` prints `0.1.0` (the tool's version) | verified |
| 4 | Without `--`, `--version` is consumed by dnx | `dnx Swarm.Cli@0.1.0 --add-source FEED --version` exits 1: `Required argument missing for option: '--version'.` plus dnx help | verified |
| 5 | Tool options such as `--out` work with or without `--` | `dnx Swarm.Cli@0.1.0 --add-source FEED -- render f.md --out DIR` writes the six files | verified |
| 6 | `--yes` is accepted by dnx before or after the package id (before `--`) and is not forwarded to the tool | `dnx Swarm.Cli@0.1.0 --yes --add-source FEED -- validate f.md` prints `ok`; also `... --add-source FEED --yes -- validate f.md` and `... FEED validate f.md --yes` print `ok` | verified |
| 7 | After `--`, `--yes` is forwarded to the tool (and the tool rejects it) | `... -- --yes validate f.md` gives tool error `unknown command '--yes'`, exit 2 | verified |
| 8 | After `--`, `--yes=true` and `-y` are forwarded untouched | `... -- --yes=true` and `... -- -y` give tool errors `unknown command '--yes=true'` / `'-y'`, exit 2 | verified |
| 9 | Tool exit codes and stderr pass through dnx unchanged | `... -- validate nope.md` exits 2 with `error: file not found: nope.md` | verified |
| 10 | `--prerelease` cannot be combined with an `@version` or `--version` | exits 1: `The --prerelease and --version options are not supported in the same command` | verified |
| 11 | A missing package or version fails with exit 1 and a message listing the searched feeds | `dnx Swarm.Cli@9.9.9 --add-source FEED -- ...` | verified |
| 12 | No prompt appears with stdin closed (no `--yes` needed) for a local-feed package | all runs above used closed stdin | verified |
| 13 | Each invocation costs roughly 8 to 25 seconds, cold or warm, dominated by feed lookups (the machine's configured remote feeds are queried too); a first-ever extract of a new version was about 14 s | timed in Git Bash and PowerShell | verified (this machine only) |
| 14 | Behaviour once the package is published to a public feed (no `--add-source`) | | unverified |
