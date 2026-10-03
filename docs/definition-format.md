---
created: 2026-10-03
updated: 2026-10-03
status: current
---

# Swarm definition format and the `swarm` tool

A swarm definition is one text file that says which roles exist, which model each runs on, which tools and gates are involved and in what order the work flows. The `swarm` tool checks the file and renders it into the files Claude Code reads: one sub-agent file per LLM role, Workflow-tool scripts for the LLM stages, and a runbook for the deterministic steps in between.

Requirement: [.NET 10 or later](https://dotnet.microsoft.com/download/dotnet/10.0). Nothing else is installed; the tool is run with `dnx`:

```bash
dnx Swarm.Cli@<version> -- validate my-swarm.md
dnx Swarm.Cli@<version> -- render my-swarm.md --out path/to/project
```

`Swarm.Cli` is a **placeholder** package id: the tool has not been published yet, so for now it is run from a local folder feed (`--add-source <folder>`). See [dnx invocation notes](dnx-invocation-notes.md) for the facts that were actually verified: `dnx.cmd` instead of `dnx` in Git Bash, the `--` separator (everything after it reaches the tool untouched; `--version` without it is eaten by dnx), why `--yes` is never used, and the stale-cache gotcha (dnx reuses an extracted version folder, so bump the version on every re-pack).

Commands (exit codes: 0 ok, 1 invalid definition, 2 usage or I/O error):

| Command | Does |
|---|---|
| `swarm validate <file>` | Parses and validates; prints `ok`. |
| `swarm render <file> --out <dir>` | Validates, then writes the agent files, scripts and runbook under `<dir>`. |
| `swarm --version`, `swarm --help` | Version / usage. |

`<file>` must end in `.md`, `.yaml` or `.yml`; the extension selects the front-end.

## 1. The model

Every format describes the same canonical model:

- **Swarm**: a name and a description.
- **Roles**: exactly one **orchestrator** (kind `code`) and any number of **`llm` roles**.
  - The orchestrator is deterministic code; it owns the **flow** and is not rendered as an agent file.
  - An `llm` role has a `model`, a `description`, a list of `tools`, a prompt, and optionally `maxTurns`, `effort`, `isolation`, `escalate-to` (the name of another `llm` role that takes over work the role marks as blocked) and `context`. Only `context: distilled` has an effect: the rendered agent file then ends with a note that the agent starts with no prior conversation.
- **Tools**: external commands run with `dnx`, each with an explicit package id, an exact pinned version and optional arguments. They are never bare names.
- **Gates**: named verification points (`kind`, optionally backed by a tool). A gate must be green before later work proceeds.
- **Flow**: the ordered stages, written on the orchestrator.

Flow entries:

| Entry | Meaning |
|---|---|
| `name*` | Fan-out: the `llm` role `name` runs once per item, in parallel. |
| `name` | The `llm` role `name` runs once over all completed items (for example a reviewer). |
| `gate:x` | Gate `x` must be green before the next stage. |
| `tool:x` | Run tool `x` (a deterministic step). |

Three front-ends build this model: Markdown (the reference format), YAML and a C# builder. They produce the same model; a test parses the sample in all three and requires identical results.

### Markdown (reference format)

The sample used throughout this page (`epic-delivery`):

```markdown
---
name: epic-delivery
description: Deliver an epic with cheap workers, on-demand experts and a batched test gate.
---
# Epic delivery swarm

## orchestrator  (code)
flow: worker*, gate:batch-green, reviewer, tool:squash

## worker  (llm)
model: haiku
description: Implements exactly one task in its own worktree and returns a fixed-shape result.
tools: Read, Edit, Write, Grep, Glob, Bash
maxTurns: 30
effort: low
isolation: worktree
escalate-to: expert
You implement one task. Run only the targeted tests. Return status, branch, commit, test summary and at most 10 lines of notes.

## expert  (llm)
model: opus
description: Solves what a worker could not, from a distilled hand-off.
tools: Read, Edit, Grep, Glob, Bash
maxTurns: 40
effort: high
context: distilled
You are given a distilled summary: goal, state, files, failed attempts with reasons, open question. Do not repeat the listed failed attempts.

## reviewer  (llm)
model: sonnet
description: Reviews a green batch diff for correctness and style.
tools: Read, Grep, Glob
maxTurns: 15
effort: medium
Review the diff. Report blocking issues first.

## tool: squash
package: Swarm.Squash
version: 0.1.0

## gate: batch-green
kind: test
tool: squash
```

Rules:

- **Front-matter** (optional block between `---` lines): `name` is required, `description` is optional (default: empty). Other front-matter keys and lines are ignored. Text outside any `##` section (a title, a preamble) is ignored.
- **Sections** start with a `##` heading, which must be one of `## name  (code)`, `## name  (llm)`, `## tool: name` or `## gate: name`. Names start with a letter and continue with letters, digits, `_` or `-`. Any other line starting with `##` followed by a space (or nothing) is an error, wherever it occurs. Lines starting with `###` are not headings.
- **Key lines** (`key: value`) come first in a section; the first line that is not a key line starts the **prompt**, which runs to the next `##` heading. Blank lines before the prompt are skipped. Values are trimmed. Lists (`tools`, `args`, `flow`) are comma-separated; an empty entry such as `a,,b` is an error.
- Allowed keys: `llm` role: `model`, `description`, `tools`, `maxTurns`, `effort`, `isolation`, `escalate-to`, `context`. `code` role: `flow` only. `tool`: `package`, `version`, `args`. `gate`: `kind`, `tool`. Unknown keys and repeated keys are errors, so a misspelling such as `escalate_to` fails instead of silently becoming prompt text. Only `llm` sections have a prompt; any other text in a `code`, `tool` or `gate` section is an error.
- If `description` is omitted on an `llm` role it defaults to `<role> role of <swarm>`.

**Caveat: a prompt whose first line looks like `Word: ...` is read as a key.** `Note: do X` as the first prompt line fails with `unknown key 'Note' in role '<role>'`, and a blank line before it does not help (blank lines before the prompt are skipped). Start the prompt with a line that is not shaped like `word:`, for example `Notes for the worker:` (the space before the colon makes it a prompt line), or reword the first sentence. Key-like lines after the first prompt line are ordinary prompt text.

### YAML

The same sample as YAML (abridged; the full file is `tests/Swarm.Tests/Samples/epic-delivery.yaml`):

```yaml
name: epic-delivery
description: Deliver an epic with cheap workers, on-demand experts and a batched test gate.
roles:
  orchestrator:
    kind: code
    flow: ["worker*", "gate:batch-green", "reviewer", "tool:squash"]
  worker:
    kind: llm
    model: haiku
    description: Implements exactly one task in its own worktree and returns a fixed-shape result.
    tools: [Read, Edit, Write, Grep, Glob, Bash]
    maxTurns: 30
    effort: low
    isolation: worktree
    escalate-to: expert
    prompt: |
      You implement one task. Run only the targeted tests. ...
  # expert and reviewer follow the same shape
tools:
  squash:
    package: Swarm.Squash
    version: 0.1.0
gates:
  batch-green:
    kind: test
    tool: squash
```

- Top-level keys: `name`, `description`, `roles`, `tools`, `gates`. Roles, tools and gates are mappings keyed by name. Every role has `kind: code|llm`. The `flow` list lives on the `code` role; `llm` roles take the same keys as in Markdown plus `prompt`.
- Unknown keys, duplicate keys and malformed YAML are errors. **Anchors, aliases and merge keys (`&x`, `*x`, `<<`) are rejected**: definitions must be explicit, so shared hidden content cannot make a definition look valid while meaning something else.
- **Empty values are rejected**: `effort:` (null or empty) fails. `""` and `[]` are allowed.
- Plain scalars are accepted as text and then validated like any other value, so `version: 1.0` fails the pinned-version rule and `model: 5` fails the model rule. `maxTurns: 30` works.

### C# builder

Reference `Swarm.Core` and `Swarm.Render` from a .NET 10 project (the builder is a library; the `swarm` command line reads only `.md`, `.yaml` and `.yml` files). This snippet builds the sample and renders it; it is checked by a test that compiles it, compares the result with the Markdown sample and verifies that this text is identical to the test's copy.

```csharp
using Swarm.Core;
using Swarm.Render;

var swarm = SwarmBuilder.Define("epic-delivery", "Deliver an epic with cheap workers, on-demand experts and a batched test gate.")
    .Orchestrator("orchestrator", "worker*", "gate:batch-green", "reviewer", "tool:squash")
    .Llm("worker", r => r
        .Model("haiku")
        .Description("Implements exactly one task in its own worktree and returns a fixed-shape result.")
        .Tools("Read", "Edit", "Write", "Grep", "Glob", "Bash")
        .MaxTurns(30).Effort("low").Isolation("worktree").EscalateTo("expert")
        .Prompt("You implement one task. Run only the targeted tests. Return status, branch, commit, test summary and at most 10 lines of notes."))
    .Llm("expert", r => r
        .Model("opus")
        .Description("Solves what a worker could not, from a distilled hand-off.")
        .Tools("Read", "Edit", "Grep", "Glob", "Bash")
        .MaxTurns(40).Effort("high").Context("distilled")
        .Prompt("You are given a distilled summary: goal, state, files, failed attempts with reasons, open question. Do not repeat the listed failed attempts."))
    .Llm("reviewer", r => r
        .Model("sonnet")
        .Description("Reviews a green batch diff for correctness and style.")
        .Tools("Read", "Grep", "Glob")
        .MaxTurns(15).Effort("medium")
        .Prompt("Review the diff. Report blocking issues first."))
    .Tool("squash", "Swarm.Squash", "0.1.0")
    .Gate("batch-green", "test", "squash")
    .Build();

// Relative path (under the output directory) to file content, as `swarm render` writes them.
var files = AgentFileRenderer.Render(swarm).Concat(WorkflowRenderer.Render(swarm)).ToList();
```

`Build()` validates and throws a `SwarmException` with the first error. Names and tool lists are trimmed; other padded values (a model of `" haiku "`) are rejected, not trimmed (see [limitations](#5-limitations-and-known-gaps)).

## 2. Validation

Validation runs in every front-end before anything is rendered. An error is one line; the CLI prints it as `error: <message>` on stderr and exits 1. Messages below are shown with example names; each is pinned verbatim by a test.

| Rule | Message |
|---|---|
| Model is required on every `llm` role | `role 'worker': missing required model` |
| Model is one of `haiku`, `sonnet`, `opus`, `fable`, `inherit`, or starts with `claude-` | `role 'worker': unknown model alias 'gpt-9' (allowed: haiku, sonnet, opus, fable, inherit or claude-<id>)` |
| Tool needs a package id | `tool 'squash': explicit package id required` |
| Tool version is pinned: `x.y.z`, optionally with a suffix such as `-rc.1` | `tool 'squash': exact pinned version required (got '0.1')` |
| Names are unique across roles, tools and gates (exact, case-sensitive match) | `duplicate name 'worker'` |
| Exactly one `code` role | `expected exactly one code orchestrator role` |
| Flow targets exist (`name*` and `name` need an `llm` role, `gate:` a gate, `tool:` a tool) | `flow stage 'ghost' (Role) does not exist` (`Fanout`, `Gate`, `Tool` for the other kinds) |
| `escalate-to` names an `llm` role | `role 'worker': escalate-to 'squash' is not an llm role` |
| A gate's `tool` exists | `gate 'batch-green': tool 'ghost' does not exist` |
| `effort` is `low`, `medium`, `high`, `xhigh` or `max` | `unknown effort 'lots' in role 'worker'` |
| `isolation` is `worktree` | `unsupported isolation 'container' in role 'worker'` |
| `maxTurns` is a positive integer | `bad maxTurns '0' in role 'worker'` |
| Gate has a `kind` | `missing required key 'kind' in gate 'batch-green'` |
| Flow entries are non-empty, without spaces, `*` or `:` in the target | `invalid flow entry 're viewer'`; `empty flow entry` (from YAML or the builder) |

Rules specific to a front-end:

| Front-end | Rule | Message |
|---|---|---|
| Markdown | `name` in front-matter | `missing front-matter key 'name'` |
| Markdown | front-matter closed | `unterminated front-matter` |
| Markdown | known heading shape | `unrecognised heading '## gate batch-green' (expected '## name (code\|llm)', '## tool: name' or '## gate: name')` |
| Markdown | known keys | `unknown key 'escalate_to' in role 'worker'`; for other sections `unknown key 'owner' in tool 'squash'` (a `code` section reads `in code 'orchestrator'`) |
| Markdown | no repeated keys | `duplicate key 'model' in 'worker'` |
| Markdown | no stray text outside `llm` sections | `unexpected text in tool 'squash': 'some text'` |
| Markdown | no empty list entries | `empty entry in list 'Read,, Write, Grep, Glob, Bash'` |
| YAML | `name` | `missing key 'name'` |
| YAML | role `kind` | `missing required key 'kind' in role 'orchestrator'`; `unknown kind 'robot' in role 'worker' (expected code or llm)` |
| YAML | no empty values | `invalid YAML (line 13): key 'effort' has no value` |
| YAML | no anchors, aliases, merge keys | `invalid YAML (line 38): YAML anchors/aliases/merge keys are not supported; write the content explicitly` |
| YAML | no repeated keys | `invalid YAML (line 14): Duplicate key effort` |
| YAML | known keys | `invalid YAML (line 14): Property 'colour' not found on type 'Swarm.Formats.YamlFrontEnd+RoleDto'.` (the type name is an internal detail that shows through) |
| YAML | no empty list entries | `empty entry in role 'reviewer' tools` (likewise `... role 'orchestrator' flow`) |
| YAML | well-formed document | `invalid YAML (line 2): While parsing a node, did not find expected node content.`; `empty or non-mapping YAML document` |
| C# builder | name, description, tool entries | `name must not be empty`; `description must not be null`; `empty tool entry in role 'w'` |

The first error found is reported. `swarm validate` and `swarm render` use the same rules; `render` adds the checks in section 3. CLI errors exit 2: `file not found: nope.md`, `unsupported file extension '.txt'; expected .md, .yaml or .yml`, `missing required option --out <dir>`, `--out requires a directory`, `unknown command 'bogus' (see --help)`.

On any failure `swarm render` writes **nothing**: both renderers run fully in memory before the first file is written, and a path that would leave `--out` is refused. (An operating-system error such as a full disk or a denied write in the middle of writing is the one case that can leave earlier files behind.) Rendering overwrites files of the same name, never deletes, so files from an earlier render under other names stay.

## 3. What `swarm render` produces

`swarm render tests/Swarm.Tests/Samples/epic-delivery.md --out <dir>` prints each path it wrote, then a reminder that agent files only reach a Claude Code session started afterwards:

```text
.claude/agents/worker.md
.claude/agents/expert.md
.claude/agents/reviewer.md
.claude/workflows/epic-delivery.1.js
.claude/workflows/epic-delivery.2.js
.claude/workflows/epic-delivery.steps.md
Note: generated agent files are only visible to a Claude Code session started after they exist; restart or open a new session.
```

Three agent files (one per `llm` role, none for the orchestrator), two workflow scripts (one per segment, see section 4) and the runbook. The agent files, script 1 and the runbook below are the real generated files, unedited; script 2 is shown in part.

### Agent files

`.claude/agents/worker.md`:

```markdown
---
name: worker
description: "Implements exactly one task in its own worktree and returns a fixed-shape result."
model: haiku
tools: Read, Edit, Write, Grep, Glob, Bash
maxTurns: 30
effort: low
isolation: worktree
---
You implement one task. Run only the targeted tests. Return status, branch, commit, test summary and at most 10 lines of notes.
```

`.claude/agents/expert.md` (note the line added by `context: distilled`):

```markdown
---
name: expert
description: "Solves what a worker could not, from a distilled hand-off."
model: opus
tools: Read, Edit, Grep, Glob, Bash
maxTurns: 40
effort: high
---
You are given a distilled summary: goal, state, files, failed attempts with reasons, open question. Do not repeat the listed failed attempts.
You start with NO prior conversation: everything you know is in the distilled hand-off you were given.
```

`reviewer.md` has the same shape (`model: sonnet`, `tools: Read, Grep, Glob`, `maxTurns: 15`, `effort: medium`).

### Workflow scripts

`.claude/workflows/epic-delivery.1.js` (fan-out segment), complete:

```javascript
export const meta = { name: "epic-delivery.1", description: "Deliver an epic with cheap workers, on-demand experts and a batched test gate.", phases: [{ title: "worker" }, { title: "expert", model: "opus" }] };

// Generated from the swarm definition; do not edit.
// Input: args.tasks, an array of task descriptions.
// Result: { halted, state, unresolved, pending, reviews }; state holds only results with status 'done'.
const RESULT = { type: 'object', properties: { status: { type: 'string' }, branch: { type: 'string' }, notes: { type: 'string' } }, required: ['status'] };

let state = args && Array.isArray(args.tasks) ? args.tasks : [];
const received = state;
const unresolved = [];
const reviews = [];
const halt = (reason) => {
  log(reason);
  return { halted: true, state: [], unresolved, pending: received, reviews, reason };
};

{
  phase("worker");
  const items = state;
  const results = await pipeline(
    items,
    (t) => agent(`Task: ${typeof t === 'string' ? t : JSON.stringify(t)}`, { agentType: "worker", model: "haiku", phase: "worker", schema: RESULT }),
    (res, t) => res && res.status === 'blocked'
      ? agent(`Task: ${JSON.stringify(t)}. A previous agent could not finish it and returned this result: ${JSON.stringify(res)}. Continue from that hand-off; do not repeat what it already tried.`, { agentType: "expert", model: "opus", phase: "expert", schema: RESULT })
      : res,
  );
  const settled = results.map((r, i) => ({ r, task: items[i] }));
  unresolved.push(...settled.filter((x) => x.r?.status !== 'done').map((x) => ({ ...(x.r ?? { status: 'failed', notes: 'agent returned no result' }), task: x.task })));
  state = settled.filter((x) => x.r?.status === 'done').map((x) => x.r);
  if (state.length === 0) return halt("worker: no task finished with status done");
}

return { halted: false, state, unresolved, pending: [], reviews };
```

`.claude/workflows/epic-delivery.2.js` (gate check, then the reviewer) takes `args.state` as input, first checks the gate evidence, then runs the reviewer as a single `agent(...)` call over all completed work and records the output in `reviews`. The gate check and stage:

```javascript
phase("gate batch-green");
if (!(args?.gates?.["batch-green"]?.green === true)) return halt("gate batch-green: evidence is missing or not green");

{
  phase("reviewer");
  if (state.length === 0) return halt("reviewer: there are no completed tasks to work on");
  const out = await agent(`You are working on these completed tasks: ${JSON.stringify(state)}. Inspect the work on each listed branch (its diff against the base) and carry out your role on it.`, { agentType: "reviewer", model: "sonnet", phase: "reviewer" });
  reviews.push({ role: "reviewer", output: out });
}
```

(The omitted parts of `epic-delivery.2.js` are the same header, `RESULT`, `state`/`unresolved`/`halt` declarations and final `return` as in script 1.)

### Runbook

`.claude/workflows/epic-delivery.steps.md`, complete:

```markdown
Run these in order. Deterministic steps run in the main session or CI; each workflow is launched with the Workflow tool and the arguments shown.
In Git Bash write `dnx.cmd` instead of `dnx`.
Save each workflow result to `.docs/runs/epic-delivery.<n>.result.json`; pass the `state` field of the previous workflow's result as the next workflow's `args.state`; tool steps that need the task list read the latest such file.

1. Run workflow `epic-delivery.1` (file `.claude/workflows/epic-delivery.1.js`; pass scriptPath if lookup by name is unavailable) with args: { "tasks": [ "<task 1>", "<task 2>" ] }. Save the result to `.docs/runs/epic-delivery.1.result.json`. If the result has halted: true, or a non-empty unresolved list: STOP and report unresolved (and pending); do not run later steps.
2. Gate "batch-green" (kind test): run dnx Swarm.Squash@0.1.0 and write the evidence JSON {"green": true|false, "summary": "..."} to .docs/runs/gates/batch-green.json, then pass it as args.gates["batch-green"]. If green is false: STOP and do not run later steps.
3. Run workflow `epic-delivery.2` (file `.claude/workflows/epic-delivery.2.js`; pass scriptPath if lookup by name is unavailable) with args: { "state": <the `state` field of the previous workflow's result>, "gates": { "batch-green": <evidence from .docs/runs/gates/batch-green.json> } }. Save the result to `.docs/runs/epic-delivery.2.result.json`. If the result has halted: true, or a non-empty unresolved list: STOP and report unresolved (and pending); do not run later steps. A rejecting review is visible in `reviews`.
4. Run: dnx Swarm.Squash@0.1.0
```

### Rules the generated files follow

- **Line endings and encoding**: every generated file is written with LF line endings, as UTF-8 without a byte-order mark. The scripts and the runbook are pure ASCII (every non-ASCII character from the definition is escaped in the scripts). Agent files carry the description and prompt as written; a prompt may contain tabs (indented code is legitimate) but any other control character is an error, and a CRLF prompt is normalised to LF.
- **Safe plain values**: in the agent front-matter, unquoted values are restricted by pattern so a value cannot split, comment out or corrupt the YAML. `model`, `effort` and `isolation` must match `[A-Za-z][A-Za-z0-9_.-]*`; each tool entry must match `[A-Za-z_][A-Za-z0-9_.:*()-]*`, which allows `Read` or `mcp__server__tool` but **rejects any entry containing a space or comma, such as `Bash(git commit:*)`**. Error: `role 'worker': field 'tools' has an unsafe value 'Bash(git commit:*)'`. The description is always written double-quoted with escapes.
- **Role names are safe file stems**: 1 to 64 letters, digits, `_` or `-`, and not a Windows reserved device name such as `con`. Error: `role 'bad name': name is not a safe file name (1-64 letters, digits, '_' or '-'; not a reserved device name)`. The swarm name and the names of gates used in the flow follow the same rule (`swarm 'bad name': ...`, `gate '...': ...`). Role names that differ only by case are rejected: `role 'Worker': name collides case-insensitively with another role`.
- **An `llm` role must list tools**: an empty `tools` list is an error, because Claude Code would give the agent every tool. Error: `role 'worker': tools list is empty (Claude Code would grant all tools); list tools explicitly`. (`swarm validate` accepts such a role; `swarm render` rejects it.) A missing model is likewise rejected rather than written empty.
- **Control characters**: `role 'worker': description contains a control character`, `role 'worker': prompt contains a control character`.
- **Runbook commands are copy-paste safe**: package ids, versions and tool arguments may contain only letters, digits and `_ . / : = @ + , -` (error: `tool 't': argument 'a b' is unsafe (allowed characters: letters, digits and _ . / : = @ + , -)`). A tool argument of `--yes` or `-y` is refused (`tool 't': argument '--yes' is not allowed (dnx must not auto-confirm)`). When a tool has arguments the command is `dnx <package>@<version> -- <args>`; the separator keeps the arguments away from dnx's own option parser.
- **Role stages**: a role stage cannot be the first stage of the first segment (it would receive raw tasks, not completed work): `role 'reviewer': a Role stage cannot be the first stage of the first segment (it would receive raw tasks, not completed work)`.

All of these render-time messages are pinned verbatim by tests.

## 4. How the output runs

### Agent files must exist before the session starts

Claude Code reads `.claude/agents/` when a session starts. A session that is already running does not see newly generated files (confirmed in the second spike's dry run, see [decisions](decisions.md)). Render first, then start (or restart) Claude Code in that project; `swarm render` prints a note saying so.

### Why the flow is split into segments

A Workflow script can fan out agents but cannot pause for a gate and cannot execute commands. Running a command through an LLM agent works but is wasteful: an `echo` run as an agent step cost about 31k tokens. So the renderer splits the flow at every `gate:` and `tool:` stage:

- Each maximal run of `name*` / `name` stages becomes one script, `.claude/workflows/<swarm>.<n>.js` (`n` counts from 1).
- Every gate and tool stage becomes a numbered step in the runbook, `.claude/workflows/<swarm>.steps.md`, interleaved with `Run workflow ...` steps. The main Claude Code session (or CI) runs the deterministic commands itself.
- Gate evidence travels into the next script through its arguments, so the script only reads it.

For the sample (`worker*, gate:batch-green, reviewer, tool:squash`) that gives two scripts and a four-step runbook: run script 1, run the gate tool, run script 2, run `squash`.

### Arguments and result of each script

| Script | Arguments |
|---|---|
| First segment | `args.tasks`: an array of task descriptions (strings; other values are passed on as JSON). |
| Later segments | `args.state`: the `state` field of the previous script's result. |
| Segment that follows a gate | `args.gates["<gate>"]` = `{ "green": true, "summary": "..." }`. Anything other than `green === true` (or a missing entry) halts the script before any agent runs. |

Each script returns `{ halted, state, unresolved, pending, reviews }`:

- `halted`: `true` when the script stopped early. A halted result also has a `reason` string and `state: []`.
- `state`: the results with `status: "done"` (each is `{ status, branch, notes }`, the shape every fan-out agent is asked to return). This is the next script's `args.state`.
- `unresolved`: every result that was not `done` (including blocked ones the expert could not finish), each with the original `task`.
- `pending`: the input the script received when it halted; `[]` otherwise.
- `reviews`: `{ role, output }` for each plain (non-fan-out) role stage, for example a reviewer's verdict.

A fan-out role with `escalate-to` hands a result with `status: "blocked"` to the named role, with the blocked result as hand-off. A script halts when no task finished as `done`.

**Scripts end with a top-level `return`.** That is verified against the real Workflow tool: a top-level `return { ... }` yields the value, while a script that wraps its work in a function and ends in a bare expression returns nothing, which would leave the runbook with no `state` to pass on.

### STOP rules

The runbook tells the operator to stop, not continue, when:

- a workflow result has `halted: true` or a non-empty `unresolved` list (report `unresolved` and `pending`); or
- a gate's evidence has `green: false`.

A rejecting review appears in `reviews`; the runbook does not stop on it automatically.

### Results and evidence on disk

By convention of the runbook (the tool does not create these folders): each workflow result is saved to `.docs/runs/<swarm>.<n>.result.json`, gate evidence to `.docs/runs/gates/<gate>.json`, and tool steps that need the task list read the latest result file.

### Verified and unverified

| Statement | Status |
|---|---|
| `dnx <id>@<version> --add-source <feed> -- <args>` passes the arguments after `--` untouched; `--yes` before `--` is consumed by dnx and never forwarded | verified, see [dnx invocation notes](dnx-invocation-notes.md) |
| A Workflow script returns the value of a top-level `return` | verified on the real Workflow tool |
| The scripts behave as described under Node with stub `agent`/`pipeline`/`phase`/`log` hooks (halt on missing or red gate, escalation, unresolved bookkeeping) | tested |
| Looking up a saved workflow **by name** | **unverified**: the runbook says to pass `scriptPath` (the file path) if lookup by name is unavailable, so use `scriptPath` |
| The scripts running end to end with real agents | **not yet exercised** (see below) |
| Running the published package without `--add-source` | unverified (not published) |

## 5. Limitations and known gaps

- **Not run end to end.** The generated scripts have been exercised under Node with stub hooks and on the real Workflow tool's zero-agent paths (returning a value), but not yet with real agents through a full fan-out, gate and review.
- **Package id is a placeholder.** `Swarm.Cli` stands in until a real id is chosen and published. The tools named in `tool:` stages (such as `Swarm.Squash` in the sample) are not built yet: of the planned tools (testgate, batch, squash) none exists, so the sample's runbook commands cannot be run today.
- **Case-insensitive duplicate names are not a validation error.** Validation compares names exactly (`Worker` and `worker` are different), but rendering rejects role names that collide case-insensitively, because they would name the same file on a case-insensitive file system (Windows, and macOS by default).
- **Self-escalation is accepted.** `escalate-to` naming the role itself passes validation.
- **`claude-` on its own is accepted as a model.** Any value starting with `claude-` is allowed; only the `[A-Za-z][A-Za-z0-9_.-]*` shape is checked later.
- **C# builder padding.** The builder trims names and tool lists but rejects, not trims, padded values elsewhere (for example `Model(" haiku ")` fails with `unknown model alias ' haiku '`). Markdown values are trimmed.
- **Markdown specifics.** Repeated keys in the front-matter overwrite silently (repeated keys in a section are an error). A prompt line starting `## ` is always read as a heading. The first-line `Word: ...` caveat above applies.
- **YAML specifics.** Empty values such as `prompt:` are rejected on purpose. Unknown-property errors show an internal type name.
- **`--out` values starting with `-`** (for example `--out -foo`) are rejected as a missing directory; use `./-foo`.
- **Tool entries with spaces** (`Bash(git commit:*)`) cannot be written yet; use the bare tool name (`Bash`).
- **Runbook wording.** For flows where a gate is directly followed by another gate or by a tool step before the next fan-out, the sentence "No workflow checks this gate, so this STOP rule is the only guard" can be inaccurate. It errs on the side of caution.
- **`--` and dnx.** The tool itself rejects a bare `--` argument; it is dnx that consumes the separator in the documented invocations.

## See also

- [dnx invocation notes](dnx-invocation-notes.md): verified facts about running the packaged tool.
- [workflow](workflow.md): the delivery design this feeds (batched testing, squash).
- [decisions](decisions.md): why deterministic steps live outside the workflow script.
- [capabilities](capabilities.md): what sub-agents and workflows can and cannot do.
