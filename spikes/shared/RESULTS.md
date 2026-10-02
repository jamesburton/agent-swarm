# Sandbox generator results

> THROWAWAY SPIKE. Host: Windows 11, 16 logical CPUs, .NET SDK 10.0.401. The box was at ~100% CPU from other
> sessions during every measurement, so all times are pessimistic and noisy.

## Generate

```powershell
cd spikes/shared
dotnet run sandbox-gen.cs -- C:\Development\agent-swarm-wt\sandbox-test --projects 12 --tests-per 20 --delay-ms 150 --seed 1 --failing 0
```

Options: `--projects --tests-per --delay-ms --seed --failing --tasks` (defaults 12 / 20 / 150 / 1 / 0 / 16).
Output: `Sandbox.slnx`, 12 libs + 12 xUnit test projects (LibK refs Lib(K-1)), `tasks.json`, `README.md`, git repo
on `main` plus 16 `task/Txxx` branches. Each task edits a distinct file, so any merge combination is textually clean.

## Failing 0

| Step | Command | Wall time |
|---|---|---|
| Generate (incl. file-app compile) | above | 1m13s to 2m28s |
| Cold restore + build + test | `dotnet test Sandbox.slnx` | 11m11s (240 tests, 0 failed) |
| Warm test only | `dotnet test Sandbox.slnx --no-build` | 1m07s (36s of pure sleep if serial) |

Cold time is dominated by NuGet restore and building on a saturated machine; use the warm figure as the suite cost.
Packages: xunit 2.9.3, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 17.14.1 (all in the local NuGet cache).

## Failing 1 (seed 1): pair T001 + T002

`--failing 1` adds `InteractionTests` in `Lib12.Tests`: T001 and T002 each set a `Code` const in their own file
to 9001; the test asserts the two codes differ. Script merged branches onto `main` and ran
`dotnet test Lib12.Tests --filter FullyQualifiedName~Interaction`:

| Merged | Result |
|---|---|
| T001 only | Passed 1/1 |
| T002 only | Passed 1/1 |
| T001 + T002 | Failed 1/1 (merge is clean, build succeeds) |

(Wall times of 80s to 245s per case are rebuild-chain cost under load, not suite time.)

## Caveats

- Same seed gives the same task/file layout and pairs; pairs are the first two tasks in different libs.
- Test classes are one class per project, so tests run serially within a project; parallelism is across projects.
- `Directory.Delete` of an existing out dir needed a read-only attribute reset for `.git` objects (fixed).

## Same-file pairs (added for spike 1B)

`--conflicts K` (K pairs rewrite the same `Note` line of one file: a real textual merge conflict), `--overlap K` (pair edits `Code` vs
`Compute` of one file: clean merge, shared `touches`), `--stacked K` (task b is branched from task a and re-edits a line: merges
clean while a is unlanded, conflicts after a squash-lands, `git rebase` is clean). Pairs take the last 2K task ids, adjacent; each pair
uses one file slot. Defaults (all 0) reproduce the old output. Verified by spike 1B runs on 12-task `--conflicts 2 --stacked 1` and
`--conflicts 1 --overlap 2 --stacked 1` sandboxes (outcomes in `../01-batched-tests/b/evidence/`).
