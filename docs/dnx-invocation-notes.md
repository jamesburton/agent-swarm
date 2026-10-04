---
created: 2026-10-03
updated: 2026-10-04
status: current
---

# dnx invocation notes

How to run the `swarm` tool through `dnx` (.NET 10 SDK 10.0.401, Windows 11). Every row was run in Git Bash (`dnx.cmd`, stdin closed; rows 1 to 4 and 7 were also run identically in PowerShell) against a real `dotnet pack` package served from a local folder feed, with stdin closed and no prompt ever appearing. `FEED` below stands for the folder holding `Swarm.Cli.0.1.0.nupkg`; `PKG` for the package id (currently the placeholder `Swarm.Cli`). Git Bash needs `dnx.cmd`; PowerShell resolves plain `dnx` to the same `dnx.cmd`.

Pack the feed: `cd src/Swarm.Cli && dotnet pack -c Release -o FEED`.

**Warning:** `Swarm.Cli` is NOT REAL: it is unclaimed on nuget.org (404 on 2026-10-03), so anyone could publish a package under it. Only run it with `--add-source` pointing at your own feed, and see the package-id warning in [the definition format](definition-format.md).

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

Each row states only what was run. `FEED` = the folder feed, `f.md` = a valid definition, `bad.md` = the same file with an unknown model alias. Commands are shown after `dnx`; "first lines" means the first lines of stdout or stderr.

| # | Fact | Command and result | Status |
|---|------|--------------------|--------|
| 1 | `--add-source` with a local folder feed resolves the package | `Swarm.Cli@0.1.0 --add-source FEED -- validate f.md` exit 0, stdout `ok`; `Swarm.Cli --version 0.1.0 --add-source FEED -- validate f.md` same | verified |
| 2 | `--` is optional for tool arguments dnx does not define | `Swarm.Cli@0.1.0 --add-source FEED validate f.md` exit 0 `ok`; `... FEED render f.md --out DIR` writes the 6 files | verified |
| 3 | After `--`, args reach the tool untouched, even ones dnx also defines | `Swarm.Cli@0.1.0 --add-source FEED -- --version` prints `0.1.0` (the tool's) | verified |
| 4 | Without `--`, `--version` is consumed by dnx | `Swarm.Cli@0.1.0 --add-source FEED --version` exit 1, stderr `Required argument missing for option: '--version'.` plus dnx help | verified |
| 5 | `--yes` and `-y` placed before the package id are accepted and not forwarded | `--yes Swarm.Cli@0.1.1 --add-source FEED -- validate f.md` exit 0 `ok`; `-y Swarm.Cli@0.1.1 ...` exit 0 `ok` | verified |
| 6 | `--yes` placed after the package id (before `--`, after `--add-source`, or trailing with no `--`) is accepted and not forwarded | `Swarm.Cli@0.1.0 --yes --add-source FEED -- validate f.md`, `Swarm.Cli@0.1.0 --add-source FEED --yes -- validate f.md`, `Swarm.Cli@0.1.0 --add-source FEED validate f.md --yes`: all exit 0 `ok` | verified |
| 7 | After `--`, `--yes`, `--yes=true`, `-y` are forwarded to the tool | `Swarm.Cli@0.1.0 --add-source FEED -- --yes validate f.md`, `-- --yes=true`, `-- -y`: exit 2, tool stderr `error: unknown command '--yes'` (resp. `'--yes=true'`, `'-y'`) | verified |
| 8 | Tool exit codes and stderr pass through dnx | `Swarm.Cli@0.1.1 --add-source FEED -- validate f.md` exit 0 `ok`; `... validate bad.md` exit 1, stderr `error: role 'expert': unknown model alias 'gpt-9' (allowed: ...)`; `... validate nope.md` exit 2, stderr `error: file not found: nope.md` | verified |
| 9 | `--prerelease` with `@version` or `--version` is rejected | `Swarm.Cli@0.1.1 --prerelease --add-source FEED -- validate f.md` and `Swarm.Cli --version 0.1.1 --prerelease --add-source FEED -- validate f.md`: exit 1, stderr `The --prerelease and --version options are not supported in the same command` | verified |
| 10 | `--prerelease` without a version runs the tool | `Swarm.Cli --prerelease --add-source FEED -- validate f.md` exit 0 `ok` (which version it picked was not recorded, so whether it prefers prerelease or stable versions is unverified) | verified (version choice unverified) |
| 11 | Missing package id | `Swarm.Nope@0.1.1 --add-source FEED -- validate f.md` exit 1, stderr starts `Version 0.1.1 of package swarm.nope is not found in NuGet feeds https://api.nuget.org/v3/index.json, ...`; without a version (`Swarm.Nope --add-source FEED -- ...`) exit 1, `swarm.nope is not found in NuGet feeds ...` | verified |
| 12 | Missing version of an existing package | `Swarm.Cli@9.9.9 --add-source FEED -- validate f.md` exit 1, stderr starts `Version 9.9.9 of package swarm.cli is not found in NuGet feeds ...` (the message does not distinguish missing id from missing version except by wording) | verified |
| 13 | A package id starting with `-` is not usable | `-x@1.0.0 --add-source FEED -- validate f.md`, `--add-source FEED -x`, and `--add-source FEED -- -x@1.0.0 validate f.md`: all exit 1, stderr ``Unhandled exception: Invalid package id : `-x`.`` (dnx treats the first non-option token, even after `--`, as the package id) | verified |
| 14 | No prompt with stdin closed for a local-feed package, with or without `--yes` | every run above | verified |
| 15 | Each invocation took 8 to 25 s, cold or warm, dominated by feed lookups (the machine's remote feeds are queried too); a first-ever extract of a new version was about 14 s | timed in Git Bash and PowerShell on this machine | verified (this machine only) |
| 16 | Behaviour once the package is published to a public feed (no `--add-source`) | not run | unverified |

## Gotcha: stale tool cache

dnx extracts packages to `~/.nuget/packages/<id>/<version>` (here `~/.nuget/packages/swarm.cli/0.1.1`) and reuses that folder. Re-packing the same version with changed code is not picked up. Verified: after dnx had run `0.1.1`, I re-packed `0.1.1` with a different informational version (`-p:Version=0.1.1 -p:InformationalVersion=repacked-different-code`) into the same feed. `dnx Swarm.Cli@0.1.1 --add-source FEED -- --version` still printed `0.1.1` (old code), while a fresh `0.1.2` packed with the same change printed `repacked-different-code`. Safe fix: bump the version for every re-pack (deleting the cached folder also works but was not tried).

## Companion tools

`Swarm.TestGate` and `Swarm.Batch` (both NOT REAL placeholder ids, version 0.1.1, packed with `dotnet pack -c Release -o FEED` into a local feed) were run through `dnx.cmd` in Git Bash with `--add-source FEED`, no `--yes`, on 2026-10-04, inside a scratch repository whose `.swarm/batch.json` set `"slots": 1` and `"testCommand": ["git", "--version"]` (so the batch suite is trivially green). Only these runs were done; PowerShell and a published feed were not tried. Tool reference: [batch-tools.md](batch-tools.md).

| # | Command (after `dnx.cmd`) | Observed |
|---|---------------------------|----------|
| 1 | `Swarm.TestGate@0.1.1 --add-source FEED -- --version` | prints `0.1.1`, exit 0 (9 s) |
| 2 | `Swarm.TestGate@0.1.1 --add-source FEED -- run -- git --version` | one JSON line with `"exitCode":0`, exit 0 (10 s) |
| 3 | `Swarm.TestGate@0.1.1 --add-source FEED -- status` | `{"schemaVersion":1,...,"slots":1,"holders":[]}`, exit 0 (9 s) |
| 4 | `Swarm.Batch@0.1.1 --add-source FEED -- run ../smoke-a-tasks.json` (scratch repo with an `epic/E1` branch and one task branch) | stderr progress, one summary line with `"tasksLanded":1,"exitCode":0`, exit 0 (23 s including first-run extraction); `git show epic/E1:one.txt` printed `one` |

Each call took 9 to 10 s on a loaded machine (rows 1 to 3); row 4 took 23 s because it included the first extraction of the `Swarm.Batch` package and the batch run itself. Both tools were bumped to 0.1.1 so a stale `~/.nuget/packages/<id>/<version>` cache could never serve old code (see the stale-cache gotcha above). Packing prints warning NU5039 (no readme); no metadata was added.

### Squash and Batch 0.2.0

`Swarm.Squash` 0.1.0 and `Swarm.Batch` 0.2.0 (both NOT REAL placeholder ids, packed with `dotnet pack src/Swarm.Squash.Cli -c Release -o FEED -warnaserror` and the same for `Swarm.Batch.Cli`; each printed the NU5039 missing-readme message) were run on 2026-10-04 the same way as above, in a second scratch repository (an `epic/E1` branch, task branches `task/9933-one` by Ada and `task/9934-two` by Bob). Only these runs were done; no prompt appeared. Timings are as recorded in the task report and were typed by hand into it. Tool reference: [squash-tool.md](squash-tool.md).

| # | Command (after `dnx.cmd`) | Observed |
|---|---------------------------|----------|
| 5 | `Swarm.Squash@0.1.0 --add-source FEED -- --version` | prints `0.1.0`, exit 0 (8 s) |
| 6 | `Swarm.Squash@0.1.0 --add-source FEED` (no arguments) | `error: Required command was not provided. (see --help)`, exit 2 (8 s) |
| 7 | `Swarm.Squash@0.1.0 --add-source FEED -- run --task T1 --branch task/9933-one` | stderr `T1: landed f0638d4... on 'epic/E1': 9933: add one`; one JSON line with `"ticket":"9933"`, `"empty":false`, `"exitCode":0`; exit 0 (30 s including first extraction) |
| 8 | `Swarm.Batch@0.2.0 --add-source FEED -- run ../smoke-b-tasks.json` (one task, `task/9934-two`) | summary with `"lander":"squash"`, `"tasksLanded":1`, `"exitCode":0`; exit 0 (34 s including first extraction) |

Afterwards `git log -2 --format='%an | %s | %(trailers:key=Ticket,valueonly)' epic/E1` printed `Bob | 9934: add two | 9934` and `Ada | 9933: add one | 9933`, and `git rev-list --merges --count main..epic/E1` printed `0`. The nuget cache directories for both packages were removed afterwards so a stale copy could not be served. Row 6 is the runbook's bare `dnx Swarm.Squash@0.1.0` step: it exits 2 and changes nothing.
