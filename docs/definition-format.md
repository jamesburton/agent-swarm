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

> **Warning: the package ids on this page are NOT REAL.** `Swarm.Cli` (the tool) and `Swarm.Squash` and `Swarm.TestGate` (the sample's tools) are not published, and nobody owns them on nuget.org: on 2026-10-03 nuget.org returned 404 for `Swarm.Cli` and `Swarm.Squash`. Anyone could publish a package under an unclaimed id, and `dnx` would download and run it (dependency confusion). Do not run these ids against nuget.org.
>
> **Review every tool package id before running a runbook.** The runbook's `dnx <package>@<version>` steps download and execute exactly the packages the definition names, so a definition is as trusted as the least trusted package in it.
>
> **Human decision before publishing:** choose and reserve an owned NuGet id prefix (nuget.org ID prefix reservation) for the tool and its companion tools, and add license and authors metadata to the packages.

Commands (exit codes: 0 ok, 1 invalid definition, 2 usage or I/O error):

| Command | Does |
|---|---|
| `swarm validate <file>` | Parses, validates and renders in memory, writing nothing; prints `ok`. `ok` means `swarm render` will accept the definition. |
| `swarm render <file> --out <dir> [--force]` | The same checks, then writes the agent files, scripts and runbook under `<dir>`. Refuses to overwrite a file that lacks the generated marker unless `--force` is given (see section 2). |
| `swarm --version`, `swarm --help` | Version / usage. |

`<file>` must end in `.md`, `.yaml` or `.yml` (the extension selects the front-end) and be at most 1 MB.

## 1. The model

Every format describes the same canonical model:

- **Swarm**: a name and a description.
- **Roles**: exactly one **orchestrator** (kind `code`) and one or more **`llm` roles**.
  - The orchestrator is deterministic code; it owns the **flow** and is not rendered as an agent file.
  - An `llm` role has a `model`, a `description`, a list of `tools`, a prompt, and optionally `maxTurns`, `effort`, `isolation`, `escalate-to` (the name of another `llm` role that takes over work the role marks as blocked) and `context`.
  - `context` accepts one value, `distilled`: the rendered agent file then ends with a note that the agent starts with no prior conversation. Other values are errors until the renderer gives them an effect.
  - Every `llm` role must be used: named by a flow stage or by some role's `escalate-to`.
- **Tools**: external commands run with `dnx`, each with an explicit NuGet package id, an exact pinned version and optional arguments. They are never bare names.
- **Gates**: named verification points (`kind`, optionally backed by a tool). A gate must be green before later work proceeds.
- **Flow**: the ordered stages (at least one), written on the orchestrator.

Flow entries:

| Entry | Meaning |
|---|---|
| `name*` | Fan-out: the `llm` role `name` runs once per item, in parallel. |
| `name` | The `llm` role `name` runs once over all completed items and returns a verdict (for example a reviewer). |
| `gate:x` | Gate `x` must be green before the next stage. |
| `tool:x` | Run tool `x` (a deterministic step). |

Three front-ends build this model: Markdown (the reference format), YAML and a C# builder. They produce the same model and the same rendered files. Parity is held by tests over the one sample in all three formats: YAML versus Markdown, the C# builder versus both, the documented builder snippet versus Markdown, and the rendered files of all three compared byte for byte.

### Markdown (reference format)

The sample used throughout this page (`epic-delivery`), exactly as in `tests/Swarm.Tests/Samples/epic-delivery.md`:

```markdown
---
name: epic-delivery
description: Deliver an epic with cheap workers, on-demand experts and a batched test gate.
---
# Epic delivery swarm

NOT REAL: the package ids Swarm.Squash and Swarm.TestGate below are examples; they are not published and nobody owns them on nuget.org. Review every tool package id before running the runbook.

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
isolation: worktree
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

## tool: testgate
package: Swarm.TestGate
version: 0.1.0

## gate: batch-green
kind: test
tool: testgate
```

The sample's gate is backed by its own `testgate` tool, so the batch is tested before the reviewer runs and `squash` runs only after an approving review.

Rules:

- **Front-matter** (required: a block between `---` lines at the top of the file) holds only `name` (required) and `description` (optional, default: empty), each at most once. Blank lines are allowed. Without the block, or without `name`, parsing fails with `missing front-matter key 'name'`; an empty `name:` fails with `swarm name must not be empty`. Any other key fails (`unknown front-matter key 'owner'`), so does a repeated key (`duplicate front-matter key 'name'`) and any line that is not `key: value` (`unexpected text in front-matter: 'hello world'`). Text outside any `##` section (a title, a preamble such as the sample's NOT REAL line) is ignored.
- **Sections** start with a `##` heading, which must be one of `## name  (code)`, `## name  (llm)`, `## tool: name` or `## gate: name`. Names start with a letter and continue with letters, digits, `_` or `-`. Any other line starting with `##` followed by a space (or nothing) is an error, wherever it occurs. Lines starting with `###` are not headings.
- **Key lines** (`key: value`) come first in a section; the first line that is not a key line starts the **prompt**, which runs to the next `##` heading. Blank lines before the prompt are skipped. Values are trimmed. Lists (`tools`, `args`, `flow`) are comma-separated; an empty entry such as `a,,b` is an error.
- Allowed keys: `llm` role: `model`, `description`, `tools`, `maxTurns`, `effort`, `isolation`, `escalate-to`, `context`. `code` role: `flow` only. `tool`: `package`, `version`, `args`. `gate`: `kind`, `tool`. Unknown keys and repeated keys are errors, so a misspelling such as `escalate_to` fails instead of silently becoming prompt text. Only `llm` sections have a prompt; any other text in a `code`, `tool` or `gate` section is an error.
- If `description` is omitted on an `llm` role it defaults to `<role> role of <swarm>`.

**Caveat: a prompt whose first line looks like `Word: ...` is read as a key.** `Note: do X` as the first prompt line fails with `unknown key 'Note' in role 'worker'`, and a blank line before it does not help (blank lines before the prompt are skipped). Start the prompt with a line that is not shaped like `word:`, for example `Notes for the worker:` (the text before the colon contains a space, so it does not match the key shape `^[A-Za-z][A-Za-z0-9_-]*:` and is read as prompt), or reword the first sentence. Key-like lines after the first prompt line are ordinary prompt text.

### YAML

The same sample as YAML (abridged; the full file is `tests/Swarm.Tests/Samples/epic-delivery.yaml`):

```yaml
# NOT REAL: the package ids Swarm.Squash and Swarm.TestGate below are examples; they are not published and nobody owns them on nuget.org. Review every tool package id before running the runbook.
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
  # expert (with isolation: worktree and context: distilled) and reviewer follow the same shape
tools:
  squash:
    package: Swarm.Squash
    version: 0.1.0
  testgate:
    package: Swarm.TestGate
    version: 0.1.0
gates:
  batch-green:
    kind: test
    tool: testgate
```

- Top-level keys: `name`, `description`, `roles`, `tools`, `gates`. Roles, tools and gates are mappings keyed by name. Every role has `kind: code|llm`. The `flow` list lives on the `code` role; `llm` roles take the same keys as in Markdown plus `prompt`.
- Unknown keys, duplicate keys and malformed YAML are errors. An unknown key is named with its owner, in the same words as Markdown (`unknown key 'colour' in role 'worker'`). **Anchors, aliases and merge keys (`&x`, `*x`, `<<`) are rejected**: definitions must be explicit, so shared hidden content cannot make a definition look valid while meaning something else.
- **Empty values are rejected**: `effort:` (null or empty) fails. `""` and `[]` are allowed.
- Plain scalars are accepted as text and then validated like any other value, so `version: 1.0` fails the pinned-version rule (`tool 'squash': exact pinned version required (got '1.0')`) and `model: 5` fails the model rule. `maxTurns: 30` works.

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
        .MaxTurns(40).Effort("high").Isolation("worktree").Context("distilled")
        .Prompt("You are given a distilled summary: goal, state, files, failed attempts with reasons, open question. Do not repeat the listed failed attempts."))
    .Llm("reviewer", r => r
        .Model("sonnet")
        .Description("Reviews a green batch diff for correctness and style.")
        .Tools("Read", "Grep", "Glob")
        .MaxTurns(15).Effort("medium")
        .Prompt("Review the diff. Report blocking issues first."))
    // NOT REAL: Swarm.Squash and Swarm.TestGate are example package ids; review every package id before running the runbook.
    .Tool("squash", "Swarm.Squash", "0.1.0")
    .Tool("testgate", "Swarm.TestGate", "0.1.0")
    .Gate("batch-green", "test", "testgate")
    .Build();

// Relative path (under the output directory) to file content, exactly as `swarm render` writes them.
var files = SwarmRenderer.Render(swarm);
```

`Build()` validates (section 2) and throws a `SwarmException` with the first error; `SwarmRenderer.Render` applies the rendering checks (section 3) and returns the files in memory. Padding is handled field by field (see [limitations](#5-limitations-and-known-gaps)): names, tool lists and the prompt are trimmed; a padded model, effort, isolation, context, `escalate-to` or tool package fails `Build()`; a padded description or gate kind is accepted by `Build()` as written (a padded gate kind then fails rendering).

## 2. Validation

Validation runs in every front-end before anything is rendered. An error is one line; the CLI prints it as `error: <message>` on stderr and exits 1. Messages below are shown with example names; each is pinned verbatim by a test.

| Rule | Message |
|---|---|
| Names are not empty (swarm, roles, tools, gates) | `swarm name must not be empty`; `role name must not be empty` (likewise `tool name must not be empty`, `gate name must not be empty`) |
| Names are unique across roles, tools and gates, ignoring case (agent and workflow files are named after them, and Windows and macOS file systems ignore case) | `duplicate name 'worker'`; `names 'worker' and 'Worker' differ only by case` |
| An `llm` role name does not read as a YAML value (it is written unquoted as the agent file's `name:`) | `role 'yes': name reads as a YAML value (null, true, false, yes, no, on, off, y, n, ~ or a number); choose another name` (any case of the words; numbers such as `42`, `1e3`, `0x1f`, `-1`) |
| Model is required on every `llm` role | `role 'worker': missing required model` |
| Model is one of `haiku`, `sonnet`, `opus`, `fable`, `inherit`, or starts with `claude-` | `role 'worker': unknown model alias 'gpt-9' (allowed: haiku, sonnet, opus, fable, inherit or claude-<id>)` |
| Every `llm` role is used by a flow stage or an `escalate-to` | `role 'reviewer' is not used by the flow or any escalate-to` |
| Tool needs a package id | `tool 'squash': explicit package id required` |
| The package id is a NuGet-style id: letters, digits, `_`, `.`, `-`, starting with a letter or digit (so it can never be read as a `dnx` option) | `tool 'squash': package 'Swarm Squash' is not a NuGet package id (letters, digits, '_', '.' or '-', starting with a letter or digit)` |
| Tool version is pinned: `x.y.z`, optionally with a suffix such as `-rc.1` | `tool 'squash': exact pinned version required (got '0.1')` |
| Exactly one `code` role | `expected exactly one code orchestrator role` |
| The flow has at least one stage | `flow is empty (the orchestrator needs at least one stage)` |
| Flow targets exist (`name*` and `name` need an `llm` role, `gate:` a gate, `tool:` a tool) | `flow stage 'ghost' (Role) does not exist` (`Fanout`, `Gate`, `Tool` for the other kinds) |
| `escalate-to` names an `llm` role | `role 'worker': escalate-to 'squash' is not an llm role` |
| A gate's `tool` exists | `gate 'batch-green': tool 'ghost' does not exist` |
| `effort` is `low`, `medium`, `high`, `xhigh` or `max` | `unknown effort 'lots' in role 'worker'` |
| `isolation` is `worktree` | `unsupported isolation 'container' in role 'worker'` |
| `context` is `distilled` | `unsupported context 'forked' in role 'expert' (allowed: distilled)` |
| `maxTurns` is a positive integer | `bad maxTurns '0' in role 'worker'` |
| Gate has a `kind` | `missing required key 'kind' in gate 'batch-green'` |
| Flow entries are non-empty, without spaces, `*` or `:` in the target | `invalid flow entry 're viewer'`; `empty flow entry` (from YAML or the builder) |

Rules specific to a front-end:

| Front-end | Rule | Message |
|---|---|---|
| Markdown | `name` in front-matter | `missing front-matter key 'name'` |
| Markdown | front-matter closed | `unterminated front-matter` |
| Markdown | front-matter holds only `name` and `description`, once each, as `key: value` lines | `unknown front-matter key 'owner'`; `duplicate front-matter key 'name'`; `unexpected text in front-matter: 'hello world'` |
| Markdown | known heading shape | `unrecognised heading '## gate batch-green' (expected '## name (code\|llm)', '## tool: name' or '## gate: name')` |
| Markdown | known keys | `unknown key 'escalate_to' in role 'worker'`; for other sections `unknown key 'owner' in tool 'squash'` (a `code` section reads `unknown key 'model' in code 'orchestrator'`) |
| Markdown | no repeated keys | `duplicate key 'model' in 'worker'` |
| Markdown | no stray text outside `llm` sections | `unexpected text in tool 'squash': 'some text'` |
| Markdown | no empty list entries | `empty entry in list 'Read,, Write, Grep, Glob, Bash'` |
| YAML | `name` | `missing key 'name'` |
| YAML | role `kind` | `missing required key 'kind' in role 'orchestrator'`; `unknown kind 'robot' in role 'worker' (expected code or llm)` |
| YAML | no empty values | `invalid YAML (line 14): key 'effort' has no value` |
| YAML | no anchors, aliases, merge keys | `invalid YAML (line 40): YAML anchors/aliases/merge keys are not supported; write the content explicitly` |
| YAML | no repeated keys | `invalid YAML (line 15): Duplicate key effort` |
| YAML | known keys | `unknown key 'colour' in role 'worker'`; `unknown key 'bogus' in tool 'squash'`; `unknown key 'extra' in gate 'batch-green'`; `unknown top-level key 'colour'` |
| YAML | code role takes only `kind` and `flow` | `unknown key 'model' in code role 'orchestrator'` |
| YAML | llm role has no `flow` | `unknown key 'flow' in llm role 'worker'` |
| YAML | wrongly typed value (for example `""` where a mapping is expected) | `invalid YAML (line 40): Exception during deserialization` |
| YAML | no empty list entries | `empty entry in role 'reviewer' tools` (for the flow: `empty entry in role 'orchestrator' flow`) |
| YAML | well-formed document | `invalid YAML (line 2): While parsing a node, did not find expected node content.`; `empty or non-mapping YAML document`. Other parser texts follow the same `invalid YAML (line N): ...` shape, or `invalid YAML: ...` without a line number |
| YAML | empty role or tool entry | `role '<name>' is empty` and `tool '<name>' is empty` exist as defensive checks, but an empty value is already reported as `key '<name>' has no value`, so they are not reachable from a text file. A tool written as `squash: {}` is not empty: it fails with `tool 'squash': explicit package id required` |
| C# builder | name, description, tool entries | `name must not be empty`; `description must not be null`; `empty tool entry in role 'w'` |
| C# builder | no null arguments | `flow must not be null`; `configuration delegate for role 'w' must not be null`; `model of role 'w' must not be null` (likewise `description of role 'w' must not be null`, and the same for `effort`, `isolation`, `escalate-to`, `context`, `prompt`); `tools of role 'w' must not be null`; `package of tool 't' must not be null`; `version of tool 't' must not be null`; `args of tool 't' must not be null`; `args of tool 't' must not contain null`; `kind of gate 'g' must not be null` |

The first error found is reported.

### `validate` means render-complete

`swarm validate` runs both renderers in memory after validation and discards their output, so it reports every error `swarm render` would report, including the rendering checks of section 3, with the same message and exit code 1. `ok` therefore means "`swarm render` will accept this definition"; only file-system problems (an unwritable `--out`, a hand-written file in the way) can still stop a render. For example `kind: unit test` on a gate fails both commands with `gate 'batch-green': kind 'unit test' is unsafe (allowed characters: letters, digits and _ . -)`.

The library is split the same way as before: `Validator`, the front-ends' `Parse` methods and `SwarmBuilder.Build()` apply the rules of this section; `SwarmRenderer.Render` (or `AgentFileRenderer.Render` and `WorkflowRenderer.Render`) applies the rendering checks of section 3. Call both, as the snippet above does, to get what `swarm validate` checks.

### Usage and I/O errors

Usage and I/O errors exit 2 (also printed as `error: <message>`):

| Cause | Message |
|---|---|
| No arguments | `missing command; expected 'validate' or 'render' (see --help)` |
| First argument is not a command | `unknown command 'bogus' (see --help)` |
| A bare `--` among the arguments | `a bare '--' is not supported (see --help)` |
| An option other than `--out` and `--force` (neither is accepted by `validate`) | `unknown option '--bogus' (see --help)`; `unknown option '--force' (see --help)` |
| A second file argument | `unexpected extra argument 'b.md'` |
| No file argument | `missing <file> argument for 'validate'` |
| `render` without `--out` | `missing required option --out <dir>` |
| `--out` last, or followed by an option such as `-foo` | `--out requires a directory` |
| `--out` given twice | `--out was given more than once` |
| `--force` given twice | `--force was given more than once` |
| `--out ""` (or whitespace) | `--out must not be empty` |
| `--out` names an existing file | `--out is a file, not a directory: <path>` |
| File missing | `file not found: nope.md` |
| File larger than 1 MB (1,048,576 bytes) | `file is larger than 1 MB: <path>` |
| Extension other than `.md`, `.yaml`, `.yml` | `unsupported file extension '.txt'; expected .md, .yaml or .yml` |
| An existing file at an output path lacks the generated marker, and `--force` was not given | `refusing to overwrite '.claude/agents/worker.md': it has no swarm:generated marker (it was not written by swarm render); use --force to overwrite it` (the first such path, in output order) |
| A rendered path would leave `--out` (not reachable with the shipped renderers) | `refusing to write outside the output directory: '../a'` |
| Any other I/O or permission failure, or an unexpected exception | the operating system's message, or `unexpected failure: <ExceptionType>: <message>` |

`--help` or `-h` anywhere in the arguments prints the usage and exits 0; so does `--version` (which prints the version).

### Writing files: nothing on failure, never over hand-written files

On any failure `swarm render` writes **nothing**: both renderers run fully in memory before the first file is written, every output path is checked to stay inside `--out`, and every file that already exists is checked for the generated marker. (An operating-system error such as a full disk or a denied write in the middle of writing is the one case that can leave earlier files behind.)

Every generated file carries a **marker line**: `<!-- swarm:generated -->` as the first line after the front-matter of an agent file and as the first line of the runbook, `// swarm:generated` as the line after the `meta` line of a script. `swarm render` overwrites an existing file only if it has a line that is exactly one of these markers, so a hand-written `.claude/agents/worker.md` (or one from another tool) is never replaced silently: the render stops with exit 2 and the message above. `--force` overwrites such files anyway. Rendering never deletes, so files from an earlier render under other names stay. Each written file is printed as `wrote <path>` (new) or `overwrote <path>` (replaced).

## 3. What `swarm render` produces

`swarm render tests/Swarm.Tests/Samples/epic-delivery.md --out <dir>` into an empty directory prints each path it wrote, then a reminder that agent files only reach a Claude Code session started afterwards:

```text
wrote .claude/agents/worker.md
wrote .claude/agents/expert.md
wrote .claude/agents/reviewer.md
wrote .claude/workflows/epic-delivery.1.js
wrote .claude/workflows/epic-delivery.2.js
wrote .claude/workflows/epic-delivery.steps.md
Note: generated agent files are only visible to a Claude Code session started after they exist; restart or open a new session.
```

Three agent files (one per `llm` role, none for the orchestrator), two workflow scripts (one per segment, see section 4) and the runbook. Every block below is the real generated file, unedited (a test compares each block with the renderer's output for the sample).

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
<!-- swarm:generated -->
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
isolation: worktree
---
<!-- swarm:generated -->
You are given a distilled summary: goal, state, files, failed attempts with reasons, open question. Do not repeat the listed failed attempts.
You start with NO prior conversation: everything you know is in the distilled hand-off you were given.
```

`.claude/agents/reviewer.md`:

```markdown
---
name: reviewer
description: "Reviews a green batch diff for correctness and style."
model: sonnet
tools: Read, Grep, Glob
maxTurns: 15
effort: medium
---
<!-- swarm:generated -->
Review the diff. Report blocking issues first.
```

### Workflow scripts

`.claude/workflows/epic-delivery.1.js` (fan-out segment):

```javascript
export const meta = { name: "epic-delivery.1", description: "Deliver an epic with cheap workers, on-demand experts and a batched test gate.", phases: [{ title: "worker" }, { title: "expert", model: "opus" }] };
// swarm:generated

// Generated from the swarm definition; do not edit.
// Input: args.tasks, an array of task descriptions.
// Result: { halted, state, unresolved, pending, reviews }; state holds only results with status 'done'; reviews holds { role, verdict, notes }.
const RESULT = { type: 'object', properties: { status: { type: 'string', enum: ['done', 'blocked', 'failed'] }, branch: { type: 'string' }, base: { type: 'string' }, notes: { type: 'string' } }, required: ['status'] };

let state = args && Array.isArray(args.tasks) ? args.tasks : [];
const received = state;
const unresolved = [];
const reviews = [];
const halt = (reason, pending = received) => {
  log(reason);
  return { halted: true, state: [], unresolved, pending, reviews, reason };
};

{
  phase("worker");
  const items = state;
  const results = await pipeline(
    items,
    (t) => agent(`Task: ${typeof t === 'string' ? t : JSON.stringify(t)}\n\nFinish with status 'done' (the task is finished and committed on your branch), 'blocked' (you cannot finish it; a stronger agent may continue from your notes) or 'failed' (it cannot be done as specified), and report your branch, the base it starts from, and short notes.`, { agentType: "worker", model: "haiku", phase: "worker", effort: "low", isolation: "worktree", schema: RESULT }),
    (res, t) => res && res.status === 'blocked'
      ? agent(`Task: ${JSON.stringify(t)}. A previous agent could not finish it and returned this result: ${JSON.stringify(res)}. Continue from that hand-off; do not repeat what it already tried.\n\nFinish with status 'done' (the task is finished and committed on your branch), 'blocked' (you cannot finish it; a stronger agent may continue from your notes) or 'failed' (it cannot be done as specified), and report your branch, the base it starts from, and short notes.`, { agentType: "expert", model: "opus", phase: "expert", effort: "high", isolation: "worktree", schema: RESULT })
      : res,
  );
  const settled = results.map((r, i) => ({ r, task: items[i] }));
  unresolved.push(...settled.filter((x) => x.r?.status !== 'done').map((x) => ({ ...(x.r ?? { status: 'failed', notes: 'agent returned no result' }), task: x.task })));
  state = settled.filter((x) => x.r?.status === 'done').map((x) => x.r);
  if (state.length === 0) return halt("worker: no task finished with status done");
}

return { halted: false, state, unresolved, pending: [], reviews };
```

`.claude/workflows/epic-delivery.2.js` (gate check, then the reviewer):

```javascript
export const meta = { name: "epic-delivery.2", description: "Deliver an epic with cheap workers, on-demand experts and a batched test gate.", phases: [{ title: "gate batch-green" }, { title: "reviewer" }] };
// swarm:generated

// Generated from the swarm definition; do not edit.
// Input: args.state, the state array of the previous workflow's result.
// Gate evidence: args.gates[<gate>] = { green, summary }, produced by the main session.
// Result: { halted, state, unresolved, pending, reviews }; state holds only results with status 'done'; reviews holds { role, verdict, notes }.
const VERDICT = { type: 'object', properties: { verdict: { type: 'string', enum: ['approve', 'reject'] }, notes: { type: 'string' } }, required: ['verdict', 'notes'] };

let state = args && Array.isArray(args.state) ? args.state : [];
const received = state;
const unresolved = [];
const reviews = [];
const halt = (reason, pending = received) => {
  log(reason);
  return { halted: true, state: [], unresolved, pending, reviews, reason };
};

phase("gate batch-green");
if (!(args?.gates?.["batch-green"]?.green === true)) return halt("gate batch-green: evidence is missing or not green");

{
  phase("reviewer");
  if (state.length === 0) return halt("reviewer: there are no completed tasks to work on");
  const out = await agent(`You are working on these completed tasks: ${JSON.stringify(state)}. Inspect the work on each listed branch (its diff against that task's reported base) and carry out your role on it. Finish with verdict 'approve' if the work may proceed, or 'reject' with the blocking issues in notes; a reject stops the run.`, { agentType: "reviewer", model: "sonnet", phase: "reviewer", effort: "medium", schema: VERDICT });
  const verdict = out?.verdict === 'approve' ? 'approve' : 'reject';
  const notes = typeof out?.notes === 'string' ? out.notes : (out ? '' : 'agent returned no verdict');
  reviews.push({ role: "reviewer", verdict, notes });
  if (verdict !== 'approve') return halt("reviewer rejected the work: " + notes, state);
}

return { halted: false, state, unresolved, pending: [], reviews };
```

### Runbook

`.claude/workflows/epic-delivery.steps.md`:

```markdown
<!-- swarm:generated -->
Run these in order. Deterministic steps run in the main session or CI; each workflow is launched with the Workflow tool and the arguments shown.
In Git Bash write `dnx.cmd` instead of `dnx`.
Save each workflow result to `.docs/runs/epic-delivery.<n>.result.json`; pass the `state` field of the previous workflow's result as the next workflow's `args.state`; tool steps that need the task list read the latest such file.

1. Run workflow `epic-delivery.1` (file `.claude/workflows/epic-delivery.1.js`; pass scriptPath if lookup by name is unavailable) with args: { "tasks": [ "<task 1>", "<task 2>" ] }. Save the result to `.docs/runs/epic-delivery.1.result.json`. If the result has halted: true, or a non-empty unresolved list: STOP and report unresolved (and pending); do not run later steps.
2. Gate "batch-green" (kind test): run dnx Swarm.TestGate@0.1.0 and write the evidence JSON {"green": true|false, "summary": "..."} to .docs/runs/gates/batch-green.json, then pass it as args.gates["batch-green"]. If green is false: STOP and do not run later steps.
3. Run workflow `epic-delivery.2` (file `.claude/workflows/epic-delivery.2.js`; pass scriptPath if lookup by name is unavailable) with args: { "state": <the `state` field of the previous workflow's result>, "gates": { "batch-green": <evidence from .docs/runs/gates/batch-green.json> } }. Save the result to `.docs/runs/epic-delivery.2.result.json`. If the result has halted: true, or a non-empty unresolved list: STOP and report unresolved (and pending); do not run later steps. A reject verdict from a review stage also halts the workflow, so this STOP rule covers it.
4. Run: dnx Swarm.Squash@0.1.0
```

### Rules the generated files follow

- **Line endings and encoding**: every generated file is written with LF line endings, as UTF-8 without a byte-order mark. The scripts and the runbook are pure ASCII (every non-ASCII character from the definition is escaped in the scripts). Agent files carry the description and prompt as written; a prompt may contain tabs (indented code is legitimate) but any other control character is an error, and a CRLF prompt is normalised to LF.
- **Marker**: every file carries the `swarm:generated` marker line described in section 2.
- **Safe plain values**: in the agent front-matter, unquoted values are restricted by pattern so a value cannot split, comment out or corrupt the YAML. `model`, `effort` and `isolation` must match `[A-Za-z][A-Za-z0-9_.-]*`; each tool entry must match `[A-Za-z_][A-Za-z0-9_.:*()-]*`, which allows `Read` or `mcp__server__tool` but **rejects any entry containing a space or comma, such as `Bash(git commit:*)`**. Error: `role 'worker': field 'tools' has an unsafe value 'Bash(git commit:*)'`. A comma inside an entry is rejected too (it would silently become two tools): `role 'worker': field 'tools' has an unsafe value 'Read,Edit'`. The description is always written double-quoted with escapes.
- **Role names are safe file stems**: 1 to 64 letters, digits, `_` or `-`, and not a Windows reserved device name such as `con`. Error: `role 'bad name': name is not a safe file name (1-64 letters, digits, '_' or '-'; not a reserved device name)`. The swarm name and the names of gates used in the flow follow the same rule (`swarm 'bad name': name is not a safe file name (1-64 letters, digits, '_' or '-'; not a reserved device name)`, `gate 'g.x': name is not a safe file name (1-64 letters, digits, '_' or '-'; not a reserved device name)`).
- **An `llm` role must list tools**: an empty `tools` list is an error, because Claude Code would give the agent every tool. Error: `role 'worker': tools list is empty (Claude Code would grant all tools); list tools explicitly`. A missing model is likewise rejected rather than written empty.
- **Control characters**: `role 'worker': description contains a control character`, `role 'worker': prompt contains a control character`. U+2028 and U+2029 (Unicode line separators) are treated like control characters in the description (same message) and in the plain fields, where the error is the unsafe-value one (`role 'worker': field 'tools' has an unsafe value 'Re?ad'`; the character is shown as `?`). A prompt body, in contrast, may contain them. A plain field other than the tool list fails the same way, for example `claude-a b` as a model: `role 'worker': field 'model' has an unsafe value 'claude-a b'`.
- **Invisible characters**: Unicode format characters (category Format: the bidi controls U+202A to U+202E and U+2066 to U+2069, the zero-width characters U+200B to U+200D, U+2060 and U+FEFF, the soft hyphen U+00AD, and the tag characters U+E0000 to U+E007F) are rejected in prompts and descriptions, because they can hide instructions from a person reviewing the definition. Errors: `role 'worker': prompt contains an invisible Unicode format character (U+200B)`, `role 'worker': description contains an invisible Unicode format character (U+202E)`, `role 'worker': prompt contains an invisible Unicode format character (U+E0041)`. Visible non-ASCII text (accents, CJK, most emoji) is kept, but the Format category also covers the zero-width joiner U+200D (used in emoji sequences such as a person-with-laptop emoji), U+200C (needed by Persian text), the tag characters of subdivision flags and the bidi marks U+200E and U+200F, so text that needs them is rejected.
- **Runbook commands are copy-paste safe**: tool arguments may contain only letters, digits and `_ . / : = @ + , -`, and a gate `kind` only letters, digits and `_ . -`. Errors: `tool 't': argument 'a b' is unsafe (allowed characters: letters, digits and _ . / : = @ + , -)` and `gate 'g': kind 'unit test' is unsafe (allowed characters: letters, digits and _ . -)`. A tool argument of `--yes` or `-y` is refused (`tool 't': argument '--yes' is not allowed (dnx must not auto-confirm)`). When a tool has arguments the command is `dnx <package>@<version> -- <args>`; the separator keeps the arguments away from dnx's own option parser.
- **Role stages**: a role stage cannot be the first stage of the first segment (it would receive raw tasks, not completed work): `role 'reviewer': a Role stage cannot be the first stage of the first segment (it would receive raw tasks, not completed work)`.

`swarm validate` reports every one of these errors too (section 2).

Definitions that skip validation (hand-built `SwarmDefinition` records passed straight to a renderer, not reachable from the three front-ends) meet extra defensive checks in the renderers: `role 'worker': model is missing`, `flow stage 'o' does not name an llm role`, `tool 't': version '1.0' is not an exact pinned version`, `tool 't': package 'P Q' is unsafe (allowed characters: letters, digits and _ . / : = @ + , -)` and `role 'Worker': name collides case-insensitively with another role`. The renderers also repeat some validation rules word for word, so such a definition gets the message validation would have given: `flow stage 'ghost' (Gate) does not exist`, `flow stage 'ghost' (Tool) does not exist` and `gate 'g': tool 'ghost' does not exist`.

All of these render-time messages are pinned verbatim by tests, and a test checks that every pinned message appears word for word in this page.

## 4. How the output runs

### Agent files must exist before the session starts

Claude Code reads `.claude/agents/` when a session starts. A session that is already running does not see newly generated files (confirmed in the second spike's dry run, see [decisions](decisions.md)). Render first, then start (or restart) Claude Code in that project; `swarm render` prints a note saying so.

### Cost: every agent has a floor

Each agent call costs about 31k to 35k tokens even for a task that uses no tools (second spike, finding 8), so a swarm's floor cost is the number of agents times about 33k tokens, before any real work. For the sample with four tasks that is at least four workers plus one reviewer, about 165k tokens, and each escalation to the expert adds another 33k or so. Fan-out width should be justified by the work, not set by default.

### Why the flow is split into segments

A Workflow script can fan out agents but cannot pause for a gate and cannot execute commands. Running a command through an LLM agent works but is wasteful: an `echo` run as an agent step cost about 31k tokens. So the renderer splits the flow at every `gate:` and `tool:` stage:

- Each maximal run of `name*` / `name` stages becomes one script, `.claude/workflows/<swarm>.<n>.js` (`n` counts from 1).
- Every gate and tool stage becomes a numbered step in the runbook, `.claude/workflows/<swarm>.steps.md`, interleaved with `Run workflow ...` steps. The main Claude Code session (or CI) runs the deterministic commands itself.
- Gate evidence travels into the next script through its arguments, so the script only reads it.

For the sample (`worker*, gate:batch-green, reviewer, tool:squash`) that gives two scripts and a four-step runbook: run script 1, run the gate's tool (`testgate`) and record its evidence, run script 2 (the review), and only then run `squash`.

### Arguments and result of each script

| Script | Arguments |
|---|---|
| First segment | `args.tasks`: an array of task descriptions (strings; other values are passed on as JSON). |
| Later segments | `args.state`: the `state` field of the previous script's result. |
| Segment that follows a gate | `args.gates["<gate>"]` = `{ "green": true, "summary": "..." }`. Anything other than `green === true` (or a missing entry) halts the script before any agent runs. |

Each script returns `{ halted, state, unresolved, pending, reviews }`:

- `halted`: `true` when the script stopped early. A halted result also has a `reason` string and `state: []`.
- `state`: the results with `status: "done"`. This is the next script's `args.state`.
- `unresolved`: every result that was not `done` (including blocked ones the expert could not finish), each with the original `task`.
- `pending`: the work the script was holding when it halted (its input, or for a rejecting review the reviewed results); `[]` otherwise.
- `reviews`: `{ role, verdict, notes }` for each plain (non-fan-out) role stage.

### The fan-out contract

Every fan-out agent (and every escalation) is called with a result schema, `{ status, branch, base, notes }`, in which `status` must be one of three values, and its prompt explains them:

- `done`: the task is finished and committed on the agent's branch;
- `blocked`: the agent cannot finish it; a stronger agent may continue from its notes (this is what triggers `escalate-to`);
- `failed`: it cannot be done as specified.

`branch` and `base` (the commit or branch the work started from) are what the reviewer diffs. A fan-out role with `escalate-to` hands a result with `status: "blocked"` to the named role, with the blocked result as hand-off. A script halts when no task finished as `done`.

**Escalation and isolation.** An escalation target called from a fan-out continues work that ran in a worktree, in parallel with other escalations, so it should set `isolation: worktree` too (the sample's `expert` does). Nothing enforces this.

### The reviewer contract

A plain role stage (such as `reviewer`) is called once with every completed result and a verdict schema, `{ verdict: 'approve' | 'reject', notes }`. Its prompt tells it to diff each task's branch against that task's reported `base`. The verdict is recorded in `reviews` as `{ role, verdict, notes }`:

- `approve`: the script continues.
- `reject`, or no usable answer at all (recorded as `reject` with notes `agent returned no verdict`): the script **halts** with `halted: true`, `reason` `<role> rejected the work: <notes>` and the reviewed results in `pending`, so the runbook's STOP rule stops before any later step (for the sample, before `squash`).

### Agent options

Each `agent(...)` call passes the role's `agentType`, `model` and `phase`, its `effort` and `isolation` when they are set, and the schema. The same `effort` and `isolation` also appear in the agent file's front-matter. Whether the Workflow tool honours `isolation` given only in an agent file's front-matter is **unverified**, which is why the scripts pass it explicitly.

**Scripts end with a top-level `return`.** That is verified against the real Workflow tool: a top-level `return { ... }` yields the value, while a script that wraps its work in a function and ends in a bare expression returns nothing, which would leave the runbook with no `state` to pass on.

### STOP rules

The runbook tells the operator to stop, not continue, when:

- a workflow result has `halted: true` (which includes a rejecting review) or a non-empty `unresolved` list (report `unresolved` and `pending`); or
- a gate's evidence has `green: false`.

### Results and evidence on disk

By convention of the runbook (the tool does not create these folders): each workflow result is saved to `.docs/runs/<swarm>.<n>.result.json`, gate evidence to `.docs/runs/gates/<gate>.json`, and tool steps that need the task list read the latest result file.

### Verified and unverified

| Statement | Status |
|---|---|
| `dnx <id>@<version> --add-source <feed> -- <args>` passes the arguments after `--` untouched; `--yes` before `--` is consumed by dnx and never forwarded | verified, see [dnx invocation notes](dnx-invocation-notes.md) |
| A Workflow script returns the value of a top-level `return` | verified on the real Workflow tool |
| The scripts behave as described under Node with stub `agent`/`pipeline`/`phase`/`log` hooks (halt on missing or red gate, escalation, unresolved bookkeeping, the status enum in the schema, approve continues and reject halts, `effort`/`isolation` in the options) | tested |
| The Workflow tool accepts `effort` and `isolation` in `agent()` options and applies them | **unverified** |
| `isolation` in an agent file's front-matter alone takes effect for Workflow agents | **unverified** |
| Real agents follow the status enum and the verdict schema | **not yet exercised** |
| Looking up a saved workflow **by name** | **unverified**: the runbook says to pass `scriptPath` (the file path) if lookup by name is unavailable, so use `scriptPath` |
| The scripts running end to end with real agents | **not yet exercised** (see below) |
| Running the published package without `--add-source` | unverified (not published) |

## 5. Limitations and known gaps

- **Not run end to end.** The generated scripts have been exercised under Node with stub hooks and on the real Workflow tool's zero-agent paths (returning a value), but not yet with real agents through a full fan-out, gate and review.
- **Package ids are placeholders and NOT REAL.** `Swarm.Cli` stands in until a real id is chosen, reserved and published (see the warning at the top). The tools named in the sample (`Swarm.TestGate`, `Swarm.Squash`) are not built yet, so the sample's runbook commands cannot be run today, and must not be run against nuget.org.
- **`escalate-to` acts only in fan-out stages.** On a role that is used only as a plain role stage (such as a reviewer), `escalate-to` is accepted but has no effect.
- **Self-escalation is accepted.** `escalate-to` naming the role itself passes validation.
- **`claude-` on its own is accepted as a model.** Any value starting with `claude-` is allowed; only the `[A-Za-z][A-Za-z0-9_.-]*` shape is checked later.
- **C# builder padding.** The builder trims names, tool entries and the prompt. A padded `Model`, `Effort`, `Isolation`, `Context`, `EscalateTo` or tool package fails `Build()` (for example `Model(" haiku ")`: `role 'w': unknown model alias ' haiku ' (allowed: haiku, sonnet, opus, fable, inherit or claude-<id>)`). A padded description or gate kind is accepted by `Build()` as written; a padded gate kind is then rejected by rendering (and so by `swarm validate`). Markdown values are trimmed.
- **Markdown specifics.** A prompt line starting `## ` is always read as a heading. The first-line `Word: ...` caveat above applies.
- **YAML specifics.** Empty values such as `prompt:` are rejected on purpose. Deeply nested YAML is not limited beyond the 1 MB input cap.
- **`--out` values starting with `-`** (for example `--out -foo`) are rejected as a missing directory; use `./-foo`.
- **Tool entries with spaces** (`Bash(git commit:*)`) cannot be written yet; use the bare tool name (`Bash`).
- **Runbook wording.** For flows where a gate is directly followed by another gate or by a tool step before the next fan-out, the sentence "No workflow checks this gate, so this STOP rule is the only guard" can be inaccurate. It errs on the side of caution.
- **`--` and dnx.** The tool itself rejects a bare `--` argument; it is dnx that consumes the separator in the documented invocations.
- **Tests need node.** The script behaviour tests run the generated scripts under node; without node on PATH they fail, unless `SWARM_ALLOW_NO_NODE=1` is set to skip them explicitly.

## See also

- [dnx invocation notes](dnx-invocation-notes.md): verified facts about running the packaged tool.
- [workflow](workflow.md): the delivery design this feeds (batched testing, squash).
- [decisions](decisions.md): why deterministic steps live outside the workflow script.
- [capabilities](capabilities.md): what sub-agents and workflows can and cannot do.
