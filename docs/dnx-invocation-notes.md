---
created: 2026-10-03
updated: 2026-10-05
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

Rows here are numbered C1, C2, ... (one sequence across the subsections below) so they never collide with the numbers of the [Verified facts](#verified-facts) table.

`Swarm.TestGate` and `Swarm.Batch` (both NOT REAL placeholder ids, version 0.1.1, packed with `dotnet pack -c Release -o FEED` into a local feed) were run through `dnx.cmd` in Git Bash with `--add-source FEED`, no `--yes`, on 2026-10-04, inside a scratch repository whose `.swarm/batch.json` set `"slots": 1` and `"testCommand": ["git", "--version"]` (so the batch suite is trivially green). Only these runs were done; PowerShell and a published feed were not tried. Tool reference: [batch-tools.md](batch-tools.md).

| # | Command (after `dnx.cmd`) | Observed |
|---|---------------------------|----------|
| C1 | `Swarm.TestGate@0.1.1 --add-source FEED -- --version` | prints `0.1.1`, exit 0 (9 s) |
| C2 | `Swarm.TestGate@0.1.1 --add-source FEED -- run -- git --version` | one JSON line with `"exitCode":0`, exit 0 (10 s) |
| C3 | `Swarm.TestGate@0.1.1 --add-source FEED -- status` | `{"schemaVersion":1,...,"slots":1,"holders":[]}`, exit 0 (9 s) |
| C4 | `Swarm.Batch@0.1.1 --add-source FEED -- run ../smoke-a-tasks.json` (scratch repo with an `epic/E1` branch and one task branch) | stderr progress, one summary line with `"tasksLanded":1,"exitCode":0`, exit 0 (23 s including first-run extraction); `git show epic/E1:one.txt` printed `one` |

Each call took 9 to 10 s on a loaded machine (rows C1 to C3); row C4 took 23 s because it included the first extraction of the `Swarm.Batch` package and the batch run itself. Both tools were bumped to 0.1.1 so a stale `~/.nuget/packages/<id>/<version>` cache could never serve old code (see the stale-cache gotcha above). Packing prints warning NU5039 (no readme); no metadata was added.

### Squash and Batch 0.2.0

`Swarm.Squash` 0.1.0 and `Swarm.Batch` 0.2.0 (both NOT REAL placeholder ids, packed with `dotnet pack src/Swarm.Squash.Cli -c Release -o FEED -warnaserror` and the same for `Swarm.Batch.Cli`; each printed the NU5039 missing-readme message) were run on 2026-10-04 the same way as above, in a second scratch repository (an `epic/E1` branch, task branches `task/9933-one` by Ada and `task/9934-two` by Bob). Only these runs were done; no prompt appeared. Timings were noted by hand. Tool reference: [squash-tool.md](squash-tool.md).

| # | Command (after `dnx.cmd`) | Observed |
|---|---------------------------|----------|
| C5 | `Swarm.Squash@0.1.0 --add-source FEED -- --version` | prints `0.1.0`, exit 0 (8 s) |
| C6 | `Swarm.Squash@0.1.0 --add-source FEED` (no arguments) | `error: Required command was not provided. (see --help)`, exit 2 (8 s) |
| C7 | `Swarm.Squash@0.1.0 --add-source FEED -- run --task T1 --branch task/9933-one` | stderr `T1: landed f0638d4... on 'epic/E1': 9933: add one`; one JSON line with `"ticket":"9933"`, `"empty":false`, `"exitCode":0`; exit 0 (30 s including first extraction) |
| C8 | `Swarm.Batch@0.2.0 --add-source FEED -- run ../smoke-b-tasks.json` (one task, `task/9934-two`) | summary with `"lander":"squash"`, `"tasksLanded":1`, `"exitCode":0`; exit 0 (34 s including first extraction) |

Afterwards `git log -2 --format='%an | %s | %(trailers:key=Ticket,valueonly)' epic/E1` printed `Bob | 9934: add two | 9934` and `Ada | 9933: add one | 9933`, and `git rev-list --merges --count main..epic/E1` printed `0`. The nuget cache directories for both packages were removed afterwards so a stale copy could not be served. Row C6 is the runbook's bare `dnx Swarm.Squash@0.1.0` step: it exits 2 and changes nothing.

### Worktree and Epic 0.1.0

On 2026-10-05 all five tools were packed from the working tree of the commit that added this section and run through `dnx.cmd` in Git Bash (stdin closed, no `--yes`, no prompt appeared) from one local feed, `.docs/feed`. Packing: `dotnet pack src/Swarm.<Tool>.Cli -c Release -o .docs/feed -warnaserror` for `TestGate`, `Batch`, `Squash`, `Worktree` and `Epic`, after deleting the old `.nupkg` files (a pack whose build was up to date did not rewrite `Swarm.Worktree.0.1.0.nupkg`). The versions were **not** bumped (`Swarm.TestGate` 0.1.2, `Swarm.Batch` 0.2.1, `Swarm.Squash` 0.1.1, `Swarm.Worktree` 0.1.0, `Swarm.Epic` 0.1.0, all NOT REAL placeholder ids); instead `~/.nuget/packages/swarm.*` was checked to be absent before the first run and deleted between the two runs and after the second, so every call extracted and ran the freshly packed code (see [the stale-cache gotcha](#gotcha-stale-tool-cache)). Tool reference: [worktree-epic-tools.md](worktree-epic-tools.md).

**This is one extended run, not the plan's scripted Task 10 Step 5.** It does everything Step 5 does (`open`, `create`, a real `squash run`, `prune --dry-run`, `prune`, `status`, `close`, the two `git log` calls) and adds `--version` of all five tools, two testgate calls, a second task landed by `batch`, `list`, `close --dry-run`, a real blocked close, `close --delete-branch`, a second close (exit 3), and a second epic whose close conflicts. The scratch repository was `<scratchpad>/smoke` (`git init -b main`, git 2.54.0.windows.1, reftable; the machine has `core.autocrlf=true` in Git for Windows' system gitconfig) with worktree root `<scratchpad>/smoke-wt`; its committed `.swarm/batch.json` was `{"slots": 1, "testCommand": ["git", "--version"], "worktree": {"branchTemplate": "task/{id}-{slug}"}, "epicTool": {"branchTemplate": "epic/{id}-{slug}"}}`, so every tool loaded a config with the two new sections. PowerShell and a published feed were not tried.

The script below made the run. `run` records each call; `D` is a `dnx.cmd` call. Rows C47 and C48 were recorded by a second, two-line script in the same repository right after it. The repository, its `-wt` folder, the tasks file and the five cache folders were deleted afterwards.

```bash
#!/usr/bin/env bash
# dnx smoke test of the five companion tools (one extended run). Writes a verbatim transcript to $T.
set -u
WT=/c/Development/agent-swarm-wt/worktree-epic
FEED="$(cd "$WT" && pwd -W)/.docs/feed"
S=<scratch>/smoke
T="$S-transcript.txt"
rm -f "$T"

# Runs one command in the current directory, stdin closed; records the command, raw stdout, raw stderr, exit code and
# wall-clock time. `FEED` in the shown command stands for the feed path.
run() {
  local shown="$*"
  shown="${shown//$FEED/FEED}"
  local t0 t1 code
  t0=$(date +%s.%N)
  "$@" </dev/null >"$S-out.txt" 2>"$S-err.txt"
  code=$?
  t1=$(date +%s.%N)
  {
    echo "\$ $shown"
    echo "[stdout]"
    cat "$S-out.txt"
    echo "[stderr]"
    cat "$S-err.txt"
    printf '[exit %s, %.1f s]\n\n' "$code" "$(awk -v a="$t0" -v b="$t1" 'BEGIN{print b-a}')"
  } >>"$T"
}

D() { run dnx.cmd "$1" --add-source "$FEED" -- "${@:2}"; }

mkdir -p "$S" && cd "$S"
git init -q -b main
git config user.name s && git config user.email s@example.invalid
mkdir .swarm
printf '%s\n' '{"slots": 1, "testCommand": ["git", "--version"], "worktree": {"branchTemplate": "task/{id}-{slug}"}, "epicTool": {"branchTemplate": "epic/{id}-{slug}"}}' > .swarm/batch.json
printf 'readme\n' > README.md
printf 'base\n' > shared.txt
git add -A && git commit -q -m init
echo "repo: $S (git $(git --version | cut -d' ' -f3), $(git rev-parse --show-ref-format))" >>"$T"
echo >>"$T"

D Swarm.TestGate@0.1.2 --version
D Swarm.Batch@0.2.1 --version
D Swarm.Squash@0.1.1 --version
D Swarm.Worktree@0.1.0 --version
D Swarm.Epic@0.1.0 --version

D Swarm.TestGate@0.1.2 run -- git --version
D Swarm.TestGate@0.1.2 status

D Swarm.Epic@0.1.0 open 42 auth
D Swarm.Worktree@0.1.0 create 9933 login --epic 42
cd "$S-wt/t-9933"
printf 'a\n' > a.txt
run git add a.txt
run git commit -q -m Login
cd "$S"
D Swarm.Squash@0.1.1 run --task T1 --branch task/9933-login --epic 42-auth

D Swarm.Worktree@0.1.0 create 9934 logout --epic 42
cd "$S-wt/t-9934"
printf 'b\n' > b.txt
run git add b.txt
run git commit -q -m Logout
cd "$S"
printf '%s\n' '[{"id":"T2","branch":"task/9934-logout"}]' > "$S-tasks.json"
D Swarm.Batch@0.2.1 run "$S-tasks.json" --epic 42-auth

D Swarm.Worktree@0.1.0 list --epic 42
D Swarm.Worktree@0.1.0 prune --dry-run
D Swarm.Worktree@0.1.0 prune
run git branch --list 'task/*'

D Swarm.Epic@0.1.0 status 42
D Swarm.Epic@0.1.0 close 42 --dry-run

# A real blocked close: a tracked edit in main, which --force cannot waive.
printf 'edited\n' > README.md
D Swarm.Epic@0.1.0 close 42 --force
# Restore the committed bytes (LF) by hand: `git checkout -- README.md` would write CRLF under the machine's
# core.autocrlf=true, which the tools' `git -c core.autocrlf=false status` can then report as modified (seen in run 1).
printf 'readme\n' > README.md
run git status --porcelain

D Swarm.Epic@0.1.0 close 42 --delete-branch
D Swarm.Epic@0.1.0 close 42

run git log --first-parent --format=%s main
run git log -1 --format=%B main
run git rev-list --parents -n 1 main
run git branch --list

# A conflict close: epic 43 changes shared.txt through a squash-landed task, main changes it too.
D Swarm.Epic@0.1.0 open 43 conflict
D Swarm.Worktree@0.1.0 create 9935 shared --epic 43
cd "$S-wt/t-9935"
printf 'epic\n' > shared.txt
run git commit -q -am "Change shared"
cd "$S"
D Swarm.Squash@0.1.1 run --task T3 --branch task/9935-shared --epic 43-conflict
printf 'main\n' > shared.txt
run git commit -q -am "Main side"
D Swarm.Epic@0.1.0 close 43
run git rev-parse main
run git worktree list --porcelain
```

Calls, in order (`FEED` stands for the feed path; exit code and wall-clock time from the transcript below):

| # | Command | Exit | Time |
|---|---------|------|------|
| C9 | `dnx.cmd Swarm.TestGate@0.1.2 --add-source FEED -- --version` | 0 | 11.7 s |
| C10 | `dnx.cmd Swarm.Batch@0.2.1 --add-source FEED -- --version` | 0 | 13.6 s |
| C11 | `dnx.cmd Swarm.Squash@0.1.1 --add-source FEED -- --version` | 0 | 7.2 s |
| C12 | `dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- --version` | 0 | 6.0 s |
| C13 | `dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- --version` | 0 | 7.1 s |
| C14 | `dnx.cmd Swarm.TestGate@0.1.2 --add-source FEED -- run -- git --version` | 0 | 8.6 s |
| C15 | `dnx.cmd Swarm.TestGate@0.1.2 --add-source FEED -- status` | 0 | 9.6 s |
| C16 | `dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- open 42 auth` | 0 | 12.1 s |
| C17 | `dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- create 9933 login --epic 42` | 0 | 12.7 s |
| C18 | `git add a.txt` | 0 | 0.7 s |
| C19 | `git commit -q -m Login` | 0 | 1.5 s |
| C20 | `dnx.cmd Swarm.Squash@0.1.1 --add-source FEED -- run --task T1 --branch task/9933-login --epic 42-auth` | 0 | 20.3 s |
| C21 | `dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- create 9934 logout --epic 42` | 0 | 11.6 s |
| C22 | `git add b.txt` | 0 | 1.1 s |
| C23 | `git commit -q -m Logout` | 0 | 1.1 s |
| C24 | `dnx.cmd Swarm.Batch@0.2.1 --add-source FEED -- run <scratch>/smoke-tasks.json --epic 42-auth` | 0 | 22.8 s |
| C25 | `dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- list --epic 42` | 0 | 12.3 s |
| C26 | `dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- prune --dry-run` | 0 | 11.8 s |
| C27 | `dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- prune` | 0 | 14.9 s |
| C28 | `git branch --list task/*` | 0 | 0.9 s |
| C29 | `dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- status 42` | 0 | 13.0 s |
| C30 | `dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42 --dry-run` | 0 | 9.1 s |
| C31 | `dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42 --force` | 1 | 12.5 s |
| C32 | `git status --porcelain` | 0 | 0.5 s |
| C33 | `dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42 --delete-branch` | 0 | 18.3 s |
| C34 | `dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42` | 3 | 5.1 s |
| C35 | `git log --first-parent --format=%s main` | 0 | 0.9 s |
| C36 | `git log -1 --format=%B main` | 0 | 0.8 s |
| C37 | `git rev-list --parents -n 1 main` | 0 | 0.5 s |
| C38 | `git branch --list` | 0 | 0.5 s |
| C39 | `dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- open 43 conflict` | 0 | 7.7 s |
| C40 | `dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- create 9935 shared --epic 43` | 0 | 7.7 s |
| C41 | `git commit -q -am Change shared` | 0 | 0.8 s |
| C42 | `dnx.cmd Swarm.Squash@0.1.1 --add-source FEED -- run --task T3 --branch task/9935-shared --epic 43-conflict` | 0 | 12.9 s |
| C43 | `git commit -q -am Main side` | 0 | 1.0 s |
| C44 | `dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 43` | 1 | 18.0 s |
| C45 | `git rev-parse main` | 0 | 0.7 s |
| C46 | `git worktree list --porcelain` | 0 | 0.8 s |
| C47 | `git log --format=%H %s -3 main` | 0 | 0.5 s |
| C48 | `git status --porcelain` | 0 | 0.5 s |

Each `dnx` call took 5.1 to 22.8 s (the first call of each package version, C9 to C13, included its extraction).

#### Transcript

Verbatim. Each call is `$ <command>` (the arguments joined by single spaces: the shell's quotes are not shown, so `--format=%H %s` was one argument and `task/*` was quoted), then its raw stdout and raw stderr, then its exit code and time.

```text
repo: <scratch>/smoke (git 2.54.0.windows.1, reftable)

$ dnx.cmd Swarm.TestGate@0.1.2 --add-source FEED -- --version
[stdout]
0.1.2
[stderr]
[exit 0, 11.7 s]

$ dnx.cmd Swarm.Batch@0.2.1 --add-source FEED -- --version
[stdout]
0.2.1
[stderr]
[exit 0, 13.6 s]

$ dnx.cmd Swarm.Squash@0.1.1 --add-source FEED -- --version
[stdout]
0.1.1
[stderr]
[exit 0, 7.2 s]

$ dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- --version
[stdout]
0.1.0
[stderr]
[exit 0, 6.0 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- --version
[stdout]
0.1.0
[stderr]
[exit 0, 7.1 s]

$ dnx.cmd Swarm.TestGate@0.1.2 --add-source FEED -- run -- git --version
[stdout]
{"schemaVersion":1,"label":"testgate","waitMs":16,"runMs":308,"slot":0,"exitCode":0,"reclaimed":false,"killed":false,"acquiredUtc":"2026-10-05T12:19:00.6349368Z","releasedUtc":"2026-10-05T12:19:01.0756761Z"}
[stderr]
git version 2.54.0.windows.1
[exit 0, 8.6 s]

$ dnx.cmd Swarm.TestGate@0.1.2 --add-source FEED -- status
[stdout]
{"schemaVersion":1,"lockDir":"<scratch>\\smoke\\.docs\\runs\\slots","slots":1,"holders":[]}
[stderr]
[exit 0, 9.6 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- open 42 auth
[stdout]
{"schemaVersion":1,"created":true,"id":"42","slug":"auth","branch":"epic/42-auth","baseBranch":"main","baseCommit":"0d4e134e3cf7217c7242b5443dd547ecf2e57b1f","batchEpic":"42-auth","warnings":[]}
[stderr]
[exit 0, 12.1 s]

$ dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- create 9933 login --epic 42
[stdout]
{"schemaVersion":1,"created":true,"path":"<scratch>\\smoke-wt\\t-9933","branch":"task/9933-login","ticket":"9933","base":"epic/42-auth","head":"0d4e134e3cf7217c7242b5443dd547ecf2e57b1f","warnings":[]}
[stderr]
[exit 0, 12.7 s]

$ git add a.txt
[stdout]
[stderr]
warning: in the working copy of 'a.txt', LF will be replaced by CRLF the next time Git touches it
[exit 0, 0.7 s]

$ git commit -q -m Login
[stdout]
[stderr]
[exit 0, 1.5 s]

$ dnx.cmd Swarm.Squash@0.1.1 --add-source FEED -- run --task T1 --branch task/9933-login --epic 42-auth
[stdout]
{"schemaVersion":1,"runId":"squash-20261005-121952-766-42-auth","task":"T1","branch":"task/9933-login","epic":"42-auth","epicBranch":"epic/42-auth","epicTipBefore":"0d4e134e3cf7217c7242b5443dd547ecf2e57b1f","epicTipAfter":"21331702ee7519dcae91f67e088fe37bfc1c0479","ticket":"9933","commit":"21331702ee7519dcae91f67e088fe37bfc1c0479","empty":false,"exitCode":0,"note":null}
[stderr]
T1: landed 21331702ee7519dcae91f67e088fe37bfc1c0479 on 'epic/42-auth': 9933: Login
[exit 0, 20.3 s]

$ dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- create 9934 logout --epic 42
[stdout]
{"schemaVersion":1,"created":true,"path":"<scratch>\\smoke-wt\\t-9934","branch":"task/9934-logout","ticket":"9934","base":"epic/42-auth","head":"21331702ee7519dcae91f67e088fe37bfc1c0479","warnings":[]}
[stderr]
[exit 0, 11.6 s]

$ git add b.txt
[stdout]
[stderr]
warning: in the working copy of 'b.txt', LF will be replaced by CRLF the next time Git touches it
[exit 0, 1.1 s]

$ git commit -q -m Logout
[stdout]
[stderr]
[exit 0, 1.1 s]

$ dnx.cmd Swarm.Batch@0.2.1 --add-source FEED -- run <scratch>/smoke-tasks.json --epic 42-auth
[stdout]
{"schemaVersion":1,"runId":"20261005-122035-566-42-auth","epic":"42-auth","epicBranch":"epic/42-auth","mode":"batched","lander":"squash","exitCode":0,"note":null,"tasks":1,"tasksLanded":1,"returned":0,"rebasedAndLanded":0,"needsWorker":0,"rejectedRed":0,"badInput":0,"unprocessed":[],"fullSuiteRuns":1,"bisectRuns":0,"inferredRedSkipped":0,"batches":1,"sizeTrace":[4],"wallSeconds":15.3,"waitMs":1,"runMs":337,"suites":[{"gate":{"schemaVersion":1,"label":"b1-batch-001","waitMs":1,"runMs":337,"slot":0,"exitCode":0,"reclaimed":false,"killed":false,"acquiredUtc":"2026-10-05T12:20:43.777582Z","releasedUtc":"2026-10-05T12:20:44.1191427Z"},"logFile":"<scratch>\\smoke\\.docs\\runs\\runs\\20261005-122035-566-42-auth\\logs\\suite-001.log","tasks":["T2"]}],"landed":[{"id":"T2","batch":1,"commit":"b3a25e7fbaaef4d21975c96d827fe1a063ead533","branch":"task/9934-logout"}],"batchLog":[{"batch":1,"bisect":false,"tasks":["T2"],"result":"green"}],"derivedTouches":{"T2":["b.txt"]},"returnedFile":"<scratch>\\smoke\\.docs\\runs\\runs\\20261005-122035-566-42-auth\\returned.jsonl","eventsFile":"<scratch>\\smoke\\.docs\\runs\\runs\\20261005-122035-566-42-auth\\events.jsonl"}
[stderr]
batch 1 size 4: T2
  suite b1-batch-001 [T2]: green (337 ms, waited 1 ms)
done: 1 of 1 landed, 0 returned, 1 suite runs (exit 0)
[exit 0, 22.8 s]

$ dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- list --epic 42
[stdout]
{"schemaVersion":1,"root":"<scratch>\\smoke-wt","worktrees":[{"path":"<scratch>\\smoke-wt\\t-9933","branch":"task/9933-login","ticket":"9933","base":"epic/42-auth","baseExists":true,"head":"2a5d32b63845c9bd1dc60b6c466e19a174d1aa4f","locked":false,"lockReason":null,"missing":false,"dirty":false,"empty":false,"aheadOfBase":1,"mergedVia":"content","managed":true,"error":null,"directoryExists":true},{"path":"<scratch>\\smoke-wt\\t-9934","branch":"task/9934-logout","ticket":"9934","base":"epic/42-auth","baseExists":true,"head":"b4451bc5aa33f0725238b92d55f9e4d3ac4211e1","locked":false,"lockReason":null,"missing":false,"dirty":false,"empty":false,"aheadOfBase":1,"mergedVia":"ledger","managed":true,"error":null,"directoryExists":true}]}
[stderr]
[exit 0, 12.3 s]

$ dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- prune --dry-run
[stdout]
{"schemaVersion":1,"dryRun":true,"force":false,"items":[{"path":"<scratch>\\smoke-wt\\t-9933","branch":"task/9933-login","ticket":"9933","action":"remove","reason":"merged (content)","done":false,"branchDeleted":false,"error":null},{"path":"<scratch>\\smoke-wt\\t-9934","branch":"task/9934-logout","ticket":"9934","action":"remove","reason":"merged (ledger)","done":false,"branchDeleted":false,"error":null}],"removed":0,"kept":0,"failed":0}
[stderr]
[exit 0, 11.8 s]

$ dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- prune
[stdout]
{"schemaVersion":1,"dryRun":false,"force":false,"items":[{"path":"<scratch>\\smoke-wt\\t-9933","branch":"task/9933-login","ticket":"9933","action":"remove","reason":"merged (content)","done":true,"branchDeleted":true,"error":null},{"path":"<scratch>\\smoke-wt\\t-9934","branch":"task/9934-logout","ticket":"9934","action":"remove","reason":"merged (ledger)","done":true,"branchDeleted":true,"error":null}],"removed":2,"kept":0,"failed":0}
[stderr]
[exit 0, 14.9 s]

$ git branch --list task/*
[stdout]
[stderr]
[exit 0, 0.9 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- status 42
[stdout]
{"schemaVersion":1,"epics":[{"schemaVersion":1,"id":"42","slug":"auth","branch":"epic/42-auth","state":"open","into":"main","tip":"b3a25e7fbaaef4d21975c96d827fe1a063ead533","ahead":2,"behind":0,"batchEpic":"42-auth","runs":1,"latestRunId":"20261005-122035-566-42-auth","latestRunExitCode":0,"landedTasks":["T2"],"openTasks":[],"worktrees":0,"worktreesUnmerged":0,"batchRunning":false,"upstream":null,"blockers":[],"readyToClose":true}]}
[stderr]
[exit 0, 13.0 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42 --dry-run
[stdout]
{"schemaVersion":1,"result":"dry-run","id":"42","branch":"epic/42-auth","into":"main","mergeCommit":null,"tickets":["9933","9934"],"blockers":[],"waived":[],"conflictFiles":[],"message":"Merge epic 42-auth (epic/42-auth) into main\n\nEpic: 42 (batch epic 42-auth)\nTickets: 9933, 9934\nRuns: 2\n\n- 9933 (manual): Login\n- 9934 (batch 1): Logout","branchDeleted":false,"warnings":[]}
[stderr]
[exit 0, 9.1 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42 --force
[stdout]
{"schemaVersion":1,"result":"blocked","id":"42","branch":"epic/42-auth","into":"main","mergeCommit":null,"tickets":["9933","9934"],"blockers":[{"code":"active-dirty","detail":"\u0027main\u0027 is checked out at \u0027<scratch>\\smoke\u0027 with uncommitted changes to tracked files","waivable":false}],"waived":[],"conflictFiles":[],"message":"Merge epic 42-auth (epic/42-auth) into main\n\nEpic: 42 (batch epic 42-auth)\nTickets: 9933, 9934\nRuns: 2\n\n- 9933 (manual): Login\n- 9934 (batch 1): Logout","branchDeleted":false,"warnings":[]}
[stderr]
error: epic '42' not closed: 'main' is checked out at '<scratch>\smoke' with uncommitted changes to tracked files (1 blocker(s); see blockers)
[exit 1, 12.5 s]

$ git status --porcelain
[stdout]
?? .docs/
[stderr]
[exit 0, 0.5 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42 --delete-branch
[stdout]
{"schemaVersion":1,"result":"merged","id":"42","branch":"epic/42-auth","into":"main","mergeCommit":"51d535858eb130b5441a66f80499086f86e273a0","tickets":["9933","9934"],"blockers":[],"waived":[],"conflictFiles":[],"message":"Merge epic 42-auth (epic/42-auth) into main\n\nEpic: 42 (batch epic 42-auth)\nTickets: 9933, 9934\nRuns: 2\n\n- 9933 (manual): Login\n- 9934 (batch 1): Logout","branchDeleted":true,"warnings":[]}
[stderr]
[exit 0, 18.3 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42
[stdout]
[stderr]
error: epic '42' is already closed (merged into 'main' at 51d535858eb130b5441a66f80499086f86e273a0)
[exit 3, 5.1 s]

$ git log --first-parent --format=%s main
[stdout]
Merge epic 42-auth (epic/42-auth) into main
init
[stderr]
[exit 0, 0.9 s]

$ git log -1 --format=%B main
[stdout]
Merge epic 42-auth (epic/42-auth) into main

Epic: 42 (batch epic 42-auth)
Tickets: 9933, 9934
Runs: 2

- 9933 (manual): Login
- 9934 (batch 1): Logout

[stderr]
[exit 0, 0.8 s]

$ git rev-list --parents -n 1 main
[stdout]
51d535858eb130b5441a66f80499086f86e273a0 0d4e134e3cf7217c7242b5443dd547ecf2e57b1f b3a25e7fbaaef4d21975c96d827fe1a063ead533
[stderr]
[exit 0, 0.5 s]

$ git branch --list
[stdout]
* main
[stderr]
[exit 0, 0.5 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- open 43 conflict
[stdout]
{"schemaVersion":1,"created":true,"id":"43","slug":"conflict","branch":"epic/43-conflict","baseBranch":"main","baseCommit":"51d535858eb130b5441a66f80499086f86e273a0","batchEpic":"43-conflict","warnings":[]}
[stderr]
[exit 0, 7.7 s]

$ dnx.cmd Swarm.Worktree@0.1.0 --add-source FEED -- create 9935 shared --epic 43
[stdout]
{"schemaVersion":1,"created":true,"path":"<scratch>\\smoke-wt\\t-9935","branch":"task/9935-shared","ticket":"9935","base":"epic/43-conflict","head":"51d535858eb130b5441a66f80499086f86e273a0","warnings":[]}
[stderr]
[exit 0, 7.7 s]

$ git commit -q -am Change shared
[stdout]
[stderr]
warning: in the working copy of '.swarm/batch.json', LF will be replaced by CRLF the next time Git touches it
warning: in the working copy of 'README.md', LF will be replaced by CRLF the next time Git touches it
warning: in the working copy of 'a.txt', LF will be replaced by CRLF the next time Git touches it
warning: in the working copy of 'b.txt', LF will be replaced by CRLF the next time Git touches it
warning: in the working copy of 'shared.txt', LF will be replaced by CRLF the next time Git touches it
[exit 0, 0.8 s]

$ dnx.cmd Swarm.Squash@0.1.1 --add-source FEED -- run --task T3 --branch task/9935-shared --epic 43-conflict
[stdout]
{"schemaVersion":1,"runId":"squash-20261005-122311-912-43-conflict","task":"T3","branch":"task/9935-shared","epic":"43-conflict","epicBranch":"epic/43-conflict","epicTipBefore":"51d535858eb130b5441a66f80499086f86e273a0","epicTipAfter":"c5001d79bab54eead46cc8190b5a4888af13646e","ticket":"9935","commit":"c5001d79bab54eead46cc8190b5a4888af13646e","empty":false,"exitCode":0,"note":null}
[stderr]
T3: landed c5001d79bab54eead46cc8190b5a4888af13646e on 'epic/43-conflict': 9935: Change shared
[exit 0, 12.9 s]

$ git commit -q -am Main side
[stdout]
[stderr]
warning: in the working copy of 'a.txt', LF will be replaced by CRLF the next time Git touches it
warning: in the working copy of 'b.txt', LF will be replaced by CRLF the next time Git touches it
warning: in the working copy of 'shared.txt', LF will be replaced by CRLF the next time Git touches it
[exit 0, 1.0 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 43
[stdout]
{"schemaVersion":1,"result":"conflict","id":"43","branch":"epic/43-conflict","into":"main","mergeCommit":null,"tickets":["9935"],"blockers":[],"waived":[],"conflictFiles":["shared.txt"],"message":"Merge epic 43-conflict (epic/43-conflict) into main\n\nEpic: 43 (batch epic 43-conflict)\nTickets: 9935\nRuns: 1\n\n- 9935 (manual): Change shared","branchDeleted":false,"warnings":[]}
[stderr]
error: merging 'epic/43-conflict' into 'main' conflicts in shared.txt (merge 'main' into the epic and resolve, then close again)
[exit 1, 18.0 s]

$ git rev-parse main
[stdout]
7c33ebf5f3ba649182b9885cd93f49d076c812e0
[stderr]
[exit 0, 0.7 s]

$ git worktree list --porcelain
[stdout]
worktree <scratch>/smoke
HEAD 7c33ebf5f3ba649182b9885cd93f49d076c812e0
branch refs/heads/main

worktree <scratch>/smoke-wt/int-42-auth
HEAD b3a25e7fbaaef4d21975c96d827fe1a063ead533
detached

worktree <scratch>/smoke-wt/int-43-conflict
HEAD c5001d79bab54eead46cc8190b5a4888af13646e
detached

worktree <scratch>/smoke-wt/t-9935
HEAD c2f9984a9f1c9454f021a1928bcbe58752ea83a4
branch refs/heads/task/9935-shared

[stderr]
[exit 0, 0.8 s]

$ git log --format=%H %s -3 main
[stdout]
7c33ebf5f3ba649182b9885cd93f49d076c812e0 Main side
51d535858eb130b5441a66f80499086f86e273a0 Merge epic 42-auth (epic/42-auth) into main
b3a25e7fbaaef4d21975c96d827fe1a063ead533 9934: Logout
[stderr]
[exit 0, 0.5 s]

$ git status --porcelain
[stdout]
?? .docs/
[stderr]
[exit 0, 0.5 s]
```

#### What the run showed

- Every output the docs describe was observed, with the full SHAs above: `list` reports `"mergedVia":"content"` for the `squash run` landing (no run state) and `"mergedVia":"ledger"` for the `batch` landing; `prune` removed both worktrees and their branches (C26, C27); `status` reported `"readyToClose":true` with the untracked `.docs/` state directory in the main worktree (C29); the merge has two parents (C37) and `git log --first-parent` reads one line per epic (C35); `--delete-branch` left only `main` (C38); the second close is exit 3 with no stdout (C34).
- **Blocked close (C31).** A tracked edit in `main` gives `active-dirty`, which `--force` does not waive (`"waived":[]`); exit 1, the JSON line, and one `error:` line.
- **Conflict close (C44).** Exit 1, `"result":"conflict"`, `"conflictFiles":["shared.txt"]` and the one `error:` line; `main` stayed at the `Main side` commit `7c33ebf5f3ba649182b9885cd93f49d076c812e0` (C45, C47) and no `close-43` worktree was left (C46 lists none). Neither the blocked nor the conflict result carried warnings here; that warnings print on stderr before the `error:` line is pinned by tests only (`EpicCliTests.ReportClose_*`).
- `batch` and `squash run` leave their detached integration worktrees `int-42-auth` and `int-43-conflict` behind (C46); `worktree prune` does not touch them (unmanaged).
- Files the tools check out (the task worktrees, and the fast-forward of `main` by `close`) are written with LF line endings although the machine's `core.autocrlf` is `true`, because the tools run git with `-c core.autocrlf=false` (`GitRunner`): later `git commit -a` calls warned `LF will be replaced by CRLF` for them (C41, C43).

#### Code bug found: line endings make `active-dirty` fire on a clean worktree

An earlier run (run 1, otherwise identical) restored the edited file with `git checkout -- README.md` instead of writing the committed bytes back. Under `core.autocrlf=true` that checkout writes `README.md` with CRLF while the index holds the LF blob; the user's `git status --porcelain` showed nothing, but every later `epic close` was still `blocked` by `active-dirty` (not waivable), so the epic could not be closed. Verbatim from run 1:

```text
$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42 --force
[stdout]
{"schemaVersion":1,"result":"blocked","id":"42","branch":"epic/42-auth","into":"main","mergeCommit":null,"tickets":["9933","9934"],"blockers":[{"code":"active-dirty","detail":"\u0027main\u0027 is checked out at \u0027<scratch>\\smoke\u0027 with uncommitted changes to tracked files","waivable":false}],"waived":[],"conflictFiles":[],"message":"Merge epic 42-auth (epic/42-auth) into main\n\nEpic: 42 (batch epic 42-auth)\nTickets: 9933, 9934\nRuns: 2\n\n- 9933 (manual): Login\n- 9934 (batch 1): Logout","branchDeleted":false,"warnings":[]}
[stderr]
error: epic '42' not closed: 'main' is checked out at '<scratch>\smoke' with uncommitted changes to tracked files (1 blocker(s); see blockers)
[exit 1, 8.3 s]

$ git checkout -- README.md
[stdout]
[stderr]
[exit 0, 0.6 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42 --delete-branch
[stdout]
{"schemaVersion":1,"result":"blocked","id":"42","branch":"epic/42-auth","into":"main","mergeCommit":null,"tickets":["9933","9934"],"blockers":[{"code":"active-dirty","detail":"\u0027main\u0027 is checked out at \u0027<scratch>\\smoke\u0027 with uncommitted changes to tracked files","waivable":false}],"waived":[],"conflictFiles":[],"message":"Merge epic 42-auth (epic/42-auth) into main\n\nEpic: 42 (batch epic 42-auth)\nTickets: 9933, 9934\nRuns: 2\n\n- 9933 (manual): Login\n- 9934 (batch 1): Logout","branchDeleted":false,"warnings":[]}
[stderr]
error: epic '42' not closed: 'main' is checked out at '<scratch>\smoke' with uncommitted changes to tracked files (1 blocker(s); see blockers)
[exit 1, 12.3 s]

$ dnx.cmd Swarm.Epic@0.1.0 --add-source FEED -- close 42
[stdout]
{"schemaVersion":1,"result":"blocked","id":"42","branch":"epic/42-auth","into":"main","mergeCommit":null,"tickets":["9933","9934"],"blockers":[{"code":"active-dirty","detail":"\u0027main\u0027 is checked out at \u0027<scratch>\\smoke\u0027 with uncommitted changes to tracked files","waivable":false}],"waived":[],"conflictFiles":[],"message":"Merge epic 42-auth (epic/42-auth) into main\n\nEpic: 42 (batch epic 42-auth)\nTickets: 9933, 9934\nRuns: 2\n\n- 9933 (manual): Login\n- 9934 (batch 1): Logout","branchDeleted":false,"warnings":[]}
[stderr]
error: epic '42' not closed: 'main' is checked out at '<scratch>\smoke' with uncommitted changes to tracked files (1 blocker(s); see blockers)
[exit 1, 11.5 s]
```

Cause: the tools run git with `-c core.autocrlf=false` (`GitRunner`), so the `active-dirty` check (`EpicAssessor`: `git status --porcelain --untracked-files=no` in the target's worktree) compares the CRLF file with the LF blob without conversion and calls it modified whenever git re-reads the file's content (it depends on the index's stat data, so it comes and goes). Reproduced separately in a scratch repository: after `touch README.md`, `git status --porcelain --untracked-files=no` printed nothing while `git -c core.autocrlf=false status --porcelain --untracked-files=no` printed ` M README.md`. **Fixed after this run** (Plan C final-review fix wave, not re-run with dnx): the dirty checks of the user's worktrees, `prune`'s non-forced remove and `close`'s fast-forward now run with the repository's own line-ending settings (`GitRunner.WithRepoLineEndings`; see [worktree-epic-tools.md](worktree-epic-tools.md#known-limitations)). The rest of this paragraph describes the run as it was. The `dirty` flag of `worktree list` and `prune` comes from the same runner and is probably affected too (not run). Run 2 avoided it by writing the committed bytes back (`printf 'readme\n' > README.md`); other workarounds were not tried.
