---
created: 2026-10-06
updated: 2026-10-07
status: current
---
# Publishing to nuget.org

The six dnx tools are published by the GitHub Actions workflow [`.github/workflows/publish.yml`](../.github/workflows/publish.yml), which uses nuget.org **Trusted Publishing**. GitHub issues the workflow a short-lived OIDC token, nuget.org checks it against a policy, and returns an API key that is valid for one hour. No long-lived API key is stored anywhere (**documented**: [Trusted Publishing on nuget.org](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)).

**Status:** set up, not yet run. No release has been published, so the workflow's publish path is **unverified** until the first run succeeds.

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
- Each tool keeps its own `<Version>` in its `.csproj`. All six are `0.1.0` for the first release.

## Configuration

| Where | Setting |
|---|---|
| nuget.org Trusted Publishing policy | Repository Owner `jamesburton`, Repository `agent-swarm`, Workflow File `publish.yml`, Environment `release`. Its scopes allow new packages and new versions for `AgentSwarm.*`. |
| GitHub environment `release` | Deployments only from the branch `main` and tags matching `v*`. The environment secret `NUGET_USER` holds the nuget.org profile name (not the email address). |
| GitHub environment `release`, once the repository is public | Add yourself as a required reviewer, so every publish waits for an approval after the tests pass. GitHub's free plan does not offer required reviewers on private repositories (the API returned HTTP 422 on 2026-10-06). |

- **Private repository caveat.** On a private repository the nuget.org policy is only **temporarily active for 7 days**. It becomes permanent after the first successful publish, which locks it to the repository's and owner's GitHub ids. If no publish happens in the window, restart it from the policy page. On a public repository the policy is active at once.
- **Prefix reservation (recommended).** Ask nuget.org to reserve the `AgentSwarm.` prefix ([ID prefix reservation](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation)), so nobody else can publish under it.

The workflow file name (`publish.yml`), the environment name (`release`) and the repository owner and name must match the nuget.org policy. Renaming any of them stops publishing until the policy is updated.

## Releasing

1. Bump `<Version>` in the `.csproj` of every tool that changed, and add a section to [CHANGELOG.md](../CHANGELOG.md). A tool whose version was not bumped is skipped: the push uses `--skip-duplicate`.
2. Commit and push to `main`.
3. Start the workflow in either of two ways:
   - push a tag, `git tag v0.1.0 && git push origin v0.1.0`. The tag name only triggers the run; package versions come from the `.csproj` files;
   - or run **publish** by hand from the Actions tab (`workflow_dispatch`) on `main`.
4. The `build` job (Windows) builds with `-warnaserror`, runs `Swarm.Tests` and `Swarm.Tools.Tests` (about 10 to 20 minutes), packs, and uploads the `.nupkg` files as the `packages` artifact.
5. The `publish` job (in the `release` environment) downloads the packages, exchanges the OIDC token for a temporary key with `NuGet/login@v1`, and pushes every package to nuget.org.

nuget.org validates and indexes a new package before `dnx` can find it, which usually takes a few minutes.

## First release checklist (0.1.0)

Before:

- [ ] All six `.csproj` files say `<Version>0.1.0</Version>`, and `CHANGELOG.md` has the 0.1.0 section.
- [ ] The repository is public, and `release` has a required reviewer.
- [ ] The `AgentSwarm.` prefix reservation has been requested.

Release:

- [ ] Push `main`, then tag `v0.1.0` and push the tag.
- [ ] The `build` job is green; approve the `publish` job.
- [ ] nuget.org lists all six packages at 0.1.0.

After:

- [ ] Smoke test from nuget.org (no `--add-source`) on a clean cache: `dnx <id>@0.1.0 -- --version` for all six, plus `dnx AgentSwarm.Cli@0.1.0 -- validate` on the sample. Record the run in [dnx-invocation-notes.md](dnx-invocation-notes.md).
- [ ] Replace the "not published yet" warnings in [definition-format.md](definition-format.md), [batch-tools.md](batch-tools.md), [squash-tool.md](squash-tool.md), [worktree-epic-tools.md](worktree-epic-tools.md), the sample definitions and `README.md`; date `CHANGELOG.md`; set this page's status to verified.
- [ ] Create a GitHub release for `v0.1.0` from the changelog section.
