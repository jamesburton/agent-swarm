> THROWAWAY SPIKE — probe results, 2026-10-02. Host: Windows 11, SDKs 8.0.124 / 10.0.103 / 10.0.302 / 10.0.401 / 11.0.100-rc.1; `global.json` pins 10.0.401.

| # | Probe | Result | Status |
|---|---|---|---|
| 1a | `dnx --yes` flag on SDK 10.0.401 | `dnx --help` lists no `--yes` flag (options: `--version`, `--allow-roll-forward`, `--prerelease`, `--configfile`, `--source`, `--add-source`, `-v`, `--disable-parallel`, `--ignore-failed-sources`, `--no-http-cache`, `--interactive`). | **verified** |
| 1b | Does `dnx` prompt without `--yes` (stdin closed)? | `dnx.cmd dotnetsay hi < /dev/null` exited 0 and printed the tool output; no prompt, first run 24 s (download). | **verified** (one package, 10.0.401) |
| 1c | `--yes` on 11.0.100-rc.1 | `dnx --help` shows no `--yes` either (grep for "yes" empty). Microsoft's page says `--yes` arrives in .NET 11; not in this RC. | **verified** for this RC |
| 2 | `dnx` resolves from Claude Code shells | PowerShell: `dnx` resolves to `C:\Program Files\dotnet\dnx.cmd`. Git Bash tool: bare `dnx` **fails** ("No such file or directory"); `dnx.cmd` works. | **verified** |
| 3 | Per-call `model` override | Agent call with `model: haiku` reported itself as "Haiku 4.5 / claude-haiku-4-5-20251001" in a session whose main model is Sonnet 5.5. | **verified** (self-report from system prompt) |
| 4 | Fork model equals parent | The Agent tool schema states a fork "always runs on your model — a `model` override is ignored". Not exercised separately. | **documented** |
| 5 | `PermissionRequest` hook fires in `claude -p` | Yes. With a hook returning `decision.behavior: allow`, `claude -p` (haiku, `--permission-mode default`) ran `touch probe5.txt` (file created) and the hook log recorded `tool_name`, `tool_input`, `permission_suggestions`. A read-only `echo` never reaches the hook (auto-allowed). Caveat: wall time was 237 s vs 7 s API time (an earlier identical run hit a 180 s timeout); cause unknown — hook startup, or machine load from parallel agents. | **verified** (latency unexplained)
| 6 | SDK `AgentDefinition` fields | Neither the TypeScript nor the Python `AgentDefinition` has `hooks`, `isolation` or `color`; `omitClaudeMd` is TypeScript-only (v0.3.271+); `background`, `effort`, `permissionMode`, `initialPrompt` exist in both. Per-subagent hooks are not registrable in the SDK; hooks are session-level. Source: code.claude.com agent-sdk typescript/python reference pages (extracted by a sub-agent, not byte-exact). | **documented** |

## Consequences

- Tool launches should not assume `--yes`; test a prompt-less launch per package, and use `dnx.cmd` in Git Bash contexts (or PowerShell).
- Markdown-defined sub-agents may support more frontmatter (`hooks`, `isolation`, `color`) than the SDK type; the spike 2 renderer targets Markdown agent files, not the SDK type.
- Generated hooks for sign-off are session-level, not per-sub-agent.
