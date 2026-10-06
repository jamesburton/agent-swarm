---
created: 2026-10-06
updated: 2026-10-06
status: current
---
# Publishing to nuget.org

The six dnx tools are published by the GitHub Actions workflow [`.github/workflows/publish.yml`](../.github/workflows/publish.yml), which uses nuget.org **Trusted Publishing**. GitHub issues the workflow a short-lived OIDC token, nuget.org checks it against a policy, and returns an API key that is valid for one hour. No long-lived API key is stored anywhere (**documented**: [Trusted Publishing on nuget.org](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)).

**Status: set up, not yet run.** No release has been published. Everything below the "One-time setup" heading is **unverified** until the first run succeeds.

## Packages

| Package id | Command | Project |
|---|---|---|
| `AgentSwarm.Cli` | `swarm` | `src/Swarm.Cli` |
| `AgentSwarm.TestGate` | `testgate` | `src/Swarm.TestGate.Cli` |
| `AgentSwarm.Batch` | `batch` | `src/Swarm.Batch.Cli` |
| `AgentSwarm.Squash` | `squash` | `src/Swarm.Squash.Cli` |
| `AgentSwarm.Worktree` | `worktree` | `src/Swarm.Worktree.Cli` |
| `AgentSwarm.Epic` | `epic` | `src/Swarm.Epic.Cli` |

- These are the only packable projects. Each tool package carries the internal `Swarm.*` libraries it uses inside `tools/net10.0/any/`, so the libraries are never published themselves (checked by unzipping the packed `AgentSwarm.Cli` and `AgentSwarm.Batch` on 2026-10-06; neither nuspec lists a dependency).
- Shared metadata (MIT license, authors, repository, tags, the package readme `src/PACKAGE-README.md`) is in `src/Swarm.Packaging.props`, which each `*.Cli` project imports.
- Each tool keeps its own `<Version>` in its `.csproj`.

## One-time setup

1. **nuget.org policy.** Sign in to nuget.org, open your username menu, then **Trusted Publishing**, and add a policy:

   | Field | Value |
   |---|---|
   | Repository Owner | `jamesburton` |
   | Repository | `agent-swarm` |
   | Workflow File | `publish.yml` (file name only) |
   | Environment | `release` |

   Under the policy's scopes, allow **publishing new packages** (the six ids do not exist yet) and new versions, limited by the glob `AgentSwarm.*`.
2. **Private repository caveat.** The repository is private, so the policy starts as only **temporarily active for 7 days**. It becomes permanent after the first successful publish, which locks it to the repository's and owner's GitHub ids. If no publish happens within 7 days the policy goes inactive; restart the window from the policy page and publish again.
3. **GitHub environment.** In the repository settings, create the environment `release`. Adding a required reviewer is recommended, so every publish waits for an approval after the tests pass.
4. **GitHub secret.** Add a repository (or `release` environment) secret `NUGET_USER` holding the nuget.org **profile name** (not the email address).
5. **Prefix reservation (recommended).** Ask nuget.org to reserve the `AgentSwarm.` prefix ([ID prefix reservation](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation)), so nobody else can publish under it. Until the first publish, anyone could still claim these ids.

## Releasing

1. Bump `<Version>` in the `.csproj` of every tool that changed. A tool whose version was not bumped is skipped: the push uses `--skip-duplicate`.
2. Commit and push to `main`.
3. Start the workflow in either of two ways:
   - push a tag, `git tag v2026.10.06 && git push origin v2026.10.06`. The tag name only triggers the run; it does not set any package version;
   - or run **publish** by hand from the Actions tab (`workflow_dispatch`).
4. The `build` job (Windows) builds with `-warnaserror`, runs `Swarm.Tests` and `Swarm.Tools.Tests` (about 20 minutes locally), packs, and uploads the `.nupkg` files.
5. The `publish` job (in the `release` environment) downloads the packages, exchanges the OIDC token for a temporary key with `NuGet/login@v1`, and pushes every package to nuget.org.

nuget.org validates and indexes a new package before `dnx` can find it, which usually takes a few minutes.

## Changing the setup

The workflow file name (`publish.yml`), the environment name (`release`) and the repository owner and name must match the nuget.org policy. Renaming any of them stops publishing until the policy is updated.
