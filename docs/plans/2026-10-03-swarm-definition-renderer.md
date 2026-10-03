---
created: 2026-10-03
updated: 2026-10-03
status: current
---
# Swarm Definition + Renderer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `dnx`-run tool (`swarm`) that reads a swarm definition (Markdown, YAML or C#), validates it, and renders Claude Code agent files plus a Workflow script and a tool-steps runbook.

**Architecture:** One canonical immutable model (`Swarm.Core`) with three front-ends (Markdown reference, YAML, C# fluent builder) and two renderers (agent files; Workflow script + steps runbook). Validation lives in the model, so every front-end gets identical rules. Deterministic tool steps (testgate/batch/squash) are **not** emitted into the Workflow script (it cannot exec commands); they go to a runbook for the main session/CI, and the script receives their results via `args`.

**Tech Stack:** .NET 10 (`global.json` 10.0.401), xUnit 2.9.x, YamlDotNet, System.CommandLine, NuGet tool packaging (`PackAsTool`).

**Spec:** [docs/specs/2026-10-02-agent-swarm-design.md](../specs/2026-10-02-agent-swarm-design.md); decisions: [docs/decisions.md](../decisions.md); spike evidence: [spikes/02-definition-renderer/README.md](../../spikes/02-definition-renderer/README.md) (port from `spikes/02-definition-renderer/a/render.cs`).

## Global Constraints

- .NET 10+ only; the single stated requirement is a link to .NET 10, tools run with `dnx <package-id>`.
- **No `--yes` flag** is ever emitted or documented for `dnx` (does not exist on SDK 10.0.401 / 11.0 RC; probe 0).
- **Tool references must be an explicit package id and an exact pinned version** (`1.2.3`; no `*`, ranges or bare names). Never emit `dnx <bare-name>`.
- Generated files use **LF line endings only** and ASCII-safe content (the Workflow tool rejects control characters).
- Model aliases allowed: `haiku`, `sonnet`, `opus`, `fable`, `inherit`, or a `claude-*` id.
- Agent-file frontmatter keys limited to documented fields: `name`, `description`, `model`, `tools`, `disallowedTools`, `maxTurns`, `effort`, `isolation`, `permissionMode`, `skills`, `background`, `color`.
- Invalid input exits non-zero with ONE line on stderr and writes NO files.
- Docs go in `docs/` with `created`/`updated` front-matter; `spikes/04-doc-sweeper/a/sweep.cs` must pass 0 errors.
- Commit trailers: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A definition whose flow names a role/gate/tool that does not exist — must be a validation error, not a silently skipped stage.
2. A worker that stays `blocked` after escalation must NOT reach the gate/squash stages; unresolved tasks must be returned to the caller.
3. Duplicate names across *kinds* (a role and a tool both called `squash`) are ambiguous and must be rejected.
4. Windows line endings in a definition file (CRLF) must not leak into generated output or break parsing.
5. A `tools:` or `escalate-to:` value with stray whitespace/case differences (`Read ,edit`) — normalise or reject clearly, never emit a broken agent file.

## File Structure

| Path | Responsibility |
|---|---|
| `src/Swarm.sln` | Solution |
| `src/Swarm.Core/Model.cs` | Immutable records: `Swarm`, `Role`, `Gate`, `ToolDef`, `Stage` |
| `src/Swarm.Core/Validator.cs` | All validation rules; `SwarmException` |
| `src/Swarm.Core/SwarmBuilder.cs` | C# fluent front-end |
| `src/Swarm.Formats/MarkdownFrontEnd.cs` | Parse Markdown definition -> model |
| `src/Swarm.Formats/YamlFrontEnd.cs` | Parse YAML definition -> model (YamlDotNet) |
| `src/Swarm.Render/AgentFileRenderer.cs` | One `.claude/agents/<role>.md` per LLM role |
| `src/Swarm.Render/WorkflowRenderer.cs` | `.claude/workflows/<name>.js` + `<name>.steps.md` runbook |
| `src/Swarm.Cli/Program.cs` | `swarm validate` / `swarm render` (tool command `swarm`) |
| `tests/Swarm.Tests/` | xUnit tests per component + end-to-end |
| `docs/definition-format.md` | User-facing format reference |

---

### Task 1: Solution scaffold and CLI skeleton

**Files:**
- Create: `src/Swarm.sln`, `src/Swarm.Core/Swarm.Core.csproj`, `src/Swarm.Formats/Swarm.Formats.csproj`, `src/Swarm.Render/Swarm.Render.csproj`, `src/Swarm.Cli/Swarm.Cli.csproj`, `tests/Swarm.Tests/Swarm.Tests.csproj`, `src/Swarm.Cli/Program.cs`

**Interfaces:** Produces the project graph `Core <- Formats <- Render <- Cli`, `Tests -> all`; tool command name `swarm`.

- [ ] **Step 1: Create projects**

```bash
cd C:/Development/agent-swarm && mkdir -p src tests && cd src
dotnet new sln -n Swarm
dotnet new classlib -n Swarm.Core -f net10.0
dotnet new classlib -n Swarm.Formats -f net10.0
dotnet new classlib -n Swarm.Render -f net10.0
dotnet new console -n Swarm.Cli -f net10.0
dotnet new xunit -n Swarm.Tests -o ../tests/Swarm.Tests -f net10.0
dotnet sln add Swarm.Core Swarm.Formats Swarm.Render Swarm.Cli ../tests/Swarm.Tests
dotnet add Swarm.Formats reference Swarm.Core
dotnet add Swarm.Render reference Swarm.Core
dotnet add Swarm.Cli reference Swarm.Core Swarm.Formats Swarm.Render
dotnet add ../tests/Swarm.Tests reference Swarm.Core Swarm.Formats Swarm.Render Swarm.Cli
dotnet add Swarm.Formats package YamlDotNet
dotnet add Swarm.Cli package System.CommandLine
```

- [ ] **Step 2: Set tool packaging in `src/Swarm.Cli/Swarm.Cli.csproj`** (add inside the first `<PropertyGroup>`)

```xml
<PackAsTool>true</PackAsTool>
<ToolCommandName>swarm</ToolCommandName>
<PackageId>Swarm.Cli</PackageId>
<Version>0.1.0</Version>
<ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable>
```

> **Decision for the human before publishing:** the real NuGet `PackageId` (the plan uses `Swarm.Cli` as a placeholder; check it is free and owned by you).

- [ ] **Step 3: Verify it builds**

Run: `cd C:/Development/agent-swarm/src && dotnet build Swarm.sln`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 4: Commit** `git add src tests && git commit -m "Scaffold swarm solution"`

### Task 2: Canonical model and validator

**Files:**
- Create: `src/Swarm.Core/Model.cs`, `src/Swarm.Core/Validator.cs`
- Test: `tests/Swarm.Tests/ValidatorTests.cs`

**Interfaces:**
- Produces:
  - `enum RoleKind { Code, Llm }`
  - `record Role(string Name, RoleKind Kind, string? Model, string Description, IReadOnlyList<string> Tools, int? MaxTurns, string? Effort, string? Isolation, string? EscalateTo, string? Context, string Prompt)`
  - `record ToolDef(string Name, string Package, string Version, IReadOnlyList<string> Args)`
  - `record Gate(string Name, string Kind, string? Tool)`
  - `enum StageType { Fanout, Role, Gate, Tool }`; `record Stage(StageType Type, string Target)`
  - `record Swarm(string Name, string Description, IReadOnlyList<Role> Roles, IReadOnlyList<ToolDef> Tools, IReadOnlyList<Gate> Gates, IReadOnlyList<Stage> Flow)`
  - `class SwarmException(string message) : Exception(message)`
  - `static class Validator { public static IReadOnlyList<string> Check(Swarm s); public static Swarm Validated(Swarm s); }` (`Validated` throws `SwarmException` with the first error).

- [ ] **Step 1: Write the failing tests** (`ValidatorTests.cs`)

```csharp
using Swarm.Core;
using Xunit;

public class ValidatorTests
{
    static Role Llm(string n, string? model = "haiku", string? esc = null) =>
        new(n, RoleKind.Llm, model, "d", ["Read"], null, null, null, esc, null, "prompt");
    static Role Code(string n = "orch") =>
        new(n, RoleKind.Code, null, "d", [], null, null, null, null, null, "");
    static Swarm Make(IEnumerable<Role>? roles = null, IEnumerable<ToolDef>? tools = null,
                      IEnumerable<Gate>? gates = null, IEnumerable<Stage>? flow = null) =>
        new("s", "d", (roles ?? [Code(), Llm("worker")]).ToList(), (tools ?? []).ToList(),
            (gates ?? []).ToList(), (flow ?? [new Stage(StageType.Fanout, "worker")]).ToList());

    [Fact] public void ValidSwarm_HasNoErrors() => Assert.Empty(Validator.Check(Make()));

    [Fact] public void UnknownModel_IsRejected() =>
        Assert.Contains(Validator.Check(Make([Code(), Llm("worker", "gpt5")])), e => e.Contains("unknown model alias 'gpt5'"));

    [Fact] public void MissingModel_IsRejected() =>
        Assert.Contains(Validator.Check(Make([Code(), Llm("worker", null)])), e => e.Contains("missing required model"));

    [Fact] public void ClaudeIdModel_IsAccepted() =>
        Assert.Empty(Validator.Check(Make([Code(), Llm("worker", "claude-haiku-4-5-20251001")])));

    [Fact] public void DuplicateAcrossKinds_IsRejected() =>
        Assert.Contains(Validator.Check(Make(tools: [new ToolDef("worker", "Pkg.Id", "1.0.0", [])])),
            e => e.Contains("duplicate name 'worker'"));

    [Fact] public void MissingOrchestrator_IsRejected() =>
        Assert.Contains(Validator.Check(Make([Llm("worker")])), e => e.Contains("exactly one code orchestrator"));

    [Fact] public void FlowToMissingTarget_IsRejected() =>
        Assert.Contains(Validator.Check(Make(flow: [new Stage(StageType.Gate, "nope")])), e => e.Contains("flow stage 'nope'"));

    [Theory] [InlineData("*")] [InlineData("1.*")] [InlineData("[1.0,2.0)")] [InlineData("")] [InlineData("latest")]
    public void UnpinnedToolVersion_IsRejected(string v) =>
        Assert.Contains(Validator.Check(Make(tools: [new ToolDef("squash", "Pkg.Id", v, [])])), e => e.Contains("exact pinned version"));

    [Fact] public void ToolWithoutPackage_IsRejected() =>
        Assert.Contains(Validator.Check(Make(tools: [new ToolDef("squash", "", "1.0.0", [])])), e => e.Contains("explicit package id"));

    [Fact] public void EscalateToUnknownOrNonLlm_IsRejected()
    {
        Assert.Contains(Validator.Check(Make([Code(), Llm("worker", "haiku", "ghost")])), e => e.Contains("escalate-to 'ghost'"));
        Assert.Contains(Validator.Check(Make([Code(), Llm("worker", "haiku", "orch")])), e => e.Contains("escalate-to 'orch'"));
    }

    [Fact] public void Validated_ThrowsFirstError() =>
        Assert.Throws<SwarmException>(() => Validator.Validated(Make([Llm("worker")])));
}
```

- [ ] **Step 2: Run to confirm failure**

Run: `cd C:/Development/agent-swarm && dotnet test tests/Swarm.Tests`
Expected: FAIL (types not defined / compile errors).

- [ ] **Step 3: Implement `Model.cs`** exactly the records above, and **`Validator.cs`**:

```csharp
using System.Text.RegularExpressions;
namespace Swarm.Core;

public class SwarmException(string message) : Exception(message);

public static class Validator
{
    static readonly HashSet<string> Aliases = ["haiku", "sonnet", "opus", "fable", "inherit"];
    static readonly Regex Pinned = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");

    public static IReadOnlyList<string> Check(Swarm s)
    {
        var errors = new List<string>();
        var names = s.Roles.Select(r => r.Name).Concat(s.Tools.Select(t => t.Name)).Concat(s.Gates.Select(g => g.Name));
        foreach (var dup in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
            errors.Add($"duplicate name '{dup.Key}'");

        if (s.Roles.Count(r => r.Kind == RoleKind.Code) != 1)
            errors.Add("expected exactly one code orchestrator role");

        foreach (var r in s.Roles.Where(r => r.Kind == RoleKind.Llm))
        {
            if (string.IsNullOrWhiteSpace(r.Model)) errors.Add($"role '{r.Name}': missing required model");
            else if (!Aliases.Contains(r.Model) && !r.Model.StartsWith("claude-", StringComparison.Ordinal))
                errors.Add($"role '{r.Name}': unknown model alias '{r.Model}' (allowed: haiku, sonnet, opus, fable, inherit or claude-<id>)");
            if (r.EscalateTo is { } e && s.Roles.FirstOrDefault(x => x.Name == e) is not { Kind: RoleKind.Llm })
                errors.Add($"role '{r.Name}': escalate-to '{e}' is not an llm role");
        }

        foreach (var t in s.Tools)
        {
            if (string.IsNullOrWhiteSpace(t.Package)) errors.Add($"tool '{t.Name}': explicit package id required");
            if (!Pinned.IsMatch(t.Version ?? "")) errors.Add($"tool '{t.Name}': exact pinned version required (got '{t.Version}')");
        }

        foreach (var st in s.Flow)
        {
            var ok = st.Type switch
            {
                StageType.Fanout or StageType.Role => s.Roles.Any(r => r.Name == st.Target && r.Kind == RoleKind.Llm),
                StageType.Gate => s.Gates.Any(g => g.Name == st.Target),
                StageType.Tool => s.Tools.Any(t => t.Name == st.Target),
                _ => false
            };
            if (!ok) errors.Add($"flow stage '{st.Target}' ({st.Type}) does not exist");
        }
        return errors;
    }

    public static Swarm Validated(Swarm s)
    {
        var e = Check(s);
        if (e.Count > 0) throw new SwarmException(e[0]);
        return s;
    }
}
```

Note the message for a missing orchestrator is `expected exactly one code orchestrator role`; the test matches the substring `exactly one code orchestrator`.

- [ ] **Step 4: Run to confirm pass**

Run: `dotnet test tests/Swarm.Tests --filter ValidatorTests`
Expected: all PASS.

- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add canonical model and validator"`

### Task 3: Markdown front-end

**Files:** Create `src/Swarm.Formats/MarkdownFrontEnd.cs`; Test `tests/Swarm.Tests/MarkdownFrontEndTests.cs`; Create `tests/Swarm.Tests/Samples/epic-delivery.md` (copy `spikes/02-definition-renderer/a/epic-delivery.md` and add the tool section below).

**Interfaces:**
- Consumes: Task 2 model and `Validator.Validated`.
- Produces: `static class MarkdownFrontEnd { public static Swarm Parse(string text); }` — parses, normalises CRLF to LF, validates via `Validator.Validated`. Grammar: optional `---` front-matter (`name`, `description`); `## <name>  (code|llm)` role headings; `## tool: <name>` with `package:`, `version:`, `args:` (comma list); `## gate: <name>` with `kind:` and `tool:`; code role has `flow:` as comma list where entries are `<role>*` (Fanout), `<role>` (Role), `gate:<name>`, `tool:<name>`; llm role `key: value` lines until the first non-key line, remainder is the prompt; keys `model, description, tools, maxTurns, effort, isolation, escalate-to, context`. Tool/role/gate lists are trimmed and role names matched case-sensitively.

- [ ] **Step 1: Add the tool section to the sample** (append to `Samples/epic-delivery.md`)

```markdown
## tool: squash
package: Swarm.Squash
version: 0.1.0

## gate: batch-green
kind: test
tool: squash
```

  and change the code role's `flow:` line to `flow: worker*, gate:batch-green, reviewer, tool:squash`.

- [ ] **Step 2: Write failing tests**

```csharp
using Swarm.Core; using Swarm.Formats; using Xunit;

public class MarkdownFrontEndTests
{
    static string Sample => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "epic-delivery.md"));

    [Fact] public void ParsesSample()
    {
        var s = MarkdownFrontEnd.Parse(Sample);
        Assert.Equal("epic-delivery", s.Name);
        Assert.Equal(3, s.Roles.Count(r => r.Kind == RoleKind.Llm));
        Assert.Equal("opus", s.Roles.Single(r => r.Name == "expert").Model);
        Assert.Equal("expert", s.Roles.Single(r => r.Name == "worker").EscalateTo);
        Assert.Equal(new[] { StageType.Fanout, StageType.Gate, StageType.Role, StageType.Tool }, s.Flow.Select(f => f.Type));
        Assert.Equal("0.1.0", s.Tools.Single().Version);
    }

    [Fact] public void CrlfInput_ParsesIdentically() =>
        Assert.Equal(MarkdownFrontEnd.Parse(Sample).Flow, MarkdownFrontEnd.Parse(Sample.Replace("\n", "\r\n")).Flow);

    [Fact] public void ToolsList_IsTrimmed() =>
        Assert.Equal(new[] { "Read", "Edit" },
            MarkdownFrontEnd.Parse(Sample.Replace("tools: Read, Edit, Write, Grep, Glob, Bash", "tools: Read ,  Edit")).Roles.Single(r => r.Name == "worker").Tools);

    [Fact] public void ExtraHeading_IsRejected() =>
        Assert.Throws<SwarmException>(() => MarkdownFrontEnd.Parse(Sample + "\n## Notes and ideas\nfoo: bar\n"));

    [Fact] public void BadModel_Throws() =>
        Assert.Throws<SwarmException>(() => MarkdownFrontEnd.Parse(Sample.Replace("model: haiku", "model: gpt5")));
}
```

- [ ] **Step 3: Run to verify failure** — `dotnet test tests/Swarm.Tests --filter MarkdownFrontEndTests` -> FAIL (compile).
- [ ] **Step 4: Implement** by porting `MarkdownFrontEnd` from `spikes/02-definition-renderer/a/render.cs` (lines ~112-176) into `src/Swarm.Formats/MarkdownFrontEnd.cs`: replace its exceptions with `SwarmException`, build the new records (`ToolDef`, `Gate.Tool`, `Stage`), trim list values, and call `Validator.Validated` at the end. Heading grammar errors use the spike's exact messages (`unrecognised heading '## X' (expected '## name (code|llm)', '## tool: name' or '## gate: name')`).
- [ ] **Step 5: Run to verify pass**, then commit `git commit -m "Add Markdown front-end"`.

### Task 4: YAML front-end (real YAML)

**Files:** Create `src/Swarm.Formats/YamlFrontEnd.cs`, `tests/Swarm.Tests/Samples/epic-delivery.yaml`; Test `tests/Swarm.Tests/YamlFrontEndTests.cs`.

**Interfaces:** Produces `static class YamlFrontEnd { public static Swarm Parse(string yaml); }` using YamlDotNet's `DeserializerBuilder().WithNamingConvention(HyphenatedNamingConvention.Instance)` into private DTO classes, then mapping to the Task 2 model and `Validator.Validated`. YAML shape: `name`, `description`, `roles: { <name>: { kind, model, description, tools: [..], maxTurns, effort, isolation, escalate-to, context, prompt, flow: [..] } }`, `tools: { <name>: { package, version, args: [..] } }`, `gates: { <name>: { kind, tool } }`.

- [ ] **Step 1: Write the parity test** (the key requirement: same model as Markdown)

```csharp
[Fact] public void YamlSample_EqualsMarkdownSample()
{
    var md = MarkdownFrontEnd.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "epic-delivery.md")));
    var yml = YamlFrontEnd.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "epic-delivery.yaml")));
    Assert.Equal(md.Name, yml.Name);
    Assert.Equal(md.Roles.Select(r => (r.Name, r.Kind, r.Model, r.EscalateTo)), yml.Roles.Select(r => (r.Name, r.Kind, r.Model, r.EscalateTo)));
    Assert.Equal(md.Flow, yml.Flow);
    Assert.Equal(md.Tools.Select(t => (t.Name, t.Package, t.Version)), yml.Tools.Select(t => (t.Name, t.Package, t.Version)));
}
[Fact] public void YamlUnknownModel_Throws() => Assert.Throws<SwarmException>(() => YamlFrontEnd.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "epic-delivery.yaml")).Replace("model: haiku", "model: gpt5")));
```

- [ ] **Step 2: Author `epic-delivery.yaml`** to mirror the Markdown sample (same roles, flow `["worker*", "gate:batch-green", "reviewer", "tool:squash"]` under the orchestrator role's `flow`, tool `squash` pinned `0.1.0`, gate `batch-green`).
- [ ] **Step 3: Run -> FAIL; Step 4: implement; Step 5: run -> PASS; commit** `git commit -m "Add YAML front-end"`. (Mark `Samples/*` as `CopyToOutputDirectory` in the test csproj: `<None Update="Samples\**" CopyToOutputDirectory="PreserveNewest" />`.)

### Task 5: C# fluent builder front-end

**Files:** Create `src/Swarm.Core/SwarmBuilder.cs`; Test `tests/Swarm.Tests/BuilderTests.cs`.

**Interfaces:** Produces `SwarmBuilder.Define(string name, string description)` with fluent `.Orchestrator(string name, params string[] flow)`, `.Llm(string name, Action<RoleBuilder> cfg)` (`RoleBuilder`: `.Model(s)`, `.Description(s)`, `.Tools(params string[])`, `.MaxTurns(int)`, `.Effort(s)`, `.Isolation(s)`, `.EscalateTo(s)`, `.Context(s)`, `.Prompt(s)`), `.Tool(string name, string package, string version, params string[] args)`, `.Gate(string name, string kind, string? tool = null)`, and `.Build()` returning a validated `Swarm`. Flow entries parse with the same grammar as Markdown (`name*`, `name`, `gate:x`, `tool:x`) via a shared `Swarm.Core.FlowParser.Parse(string)` that Tasks 3 and 4 also call (move the spike's stage parsing there).

- [ ] **Step 1: Test** that the builder reproduces the sample's flow and roles:

```csharp
[Fact] public void BuilderMatchesMarkdownSample()
{
    var b = SwarmBuilder.Define("epic-delivery", "d")
        .Orchestrator("orchestrator", "worker*", "gate:batch-green", "reviewer", "tool:squash")
        .Llm("worker", r => r.Model("haiku").Description("d").Tools("Read", "Edit").EscalateTo("expert").Prompt("p"))
        .Llm("expert", r => r.Model("opus").Description("d").Prompt("p"))
        .Llm("reviewer", r => r.Model("sonnet").Description("d").Prompt("p"))
        .Tool("squash", "Swarm.Squash", "0.1.0")
        .Gate("batch-green", "test", "squash")
        .Build();
    Assert.Equal(new[] { StageType.Fanout, StageType.Gate, StageType.Role, StageType.Tool }, b.Flow.Select(f => f.Type));
}
[Fact] public void BuilderRejectsBadModel() =>
    Assert.Throws<SwarmException>(() => SwarmBuilder.Define("s", "d").Orchestrator("o").Llm("w", r => r.Model("gpt5").Prompt("p")).Build());
```

- [ ] **Steps 2-5:** run (FAIL), implement, run (PASS), commit `git commit -m "Add C# builder front-end and shared flow parser"`.

### Task 6: Agent-file renderer

**Files:** Create `src/Swarm.Render/AgentFileRenderer.cs`; Test `tests/Swarm.Tests/AgentFileRendererTests.cs`.

**Interfaces:** Produces `static class AgentFileRenderer { public static IReadOnlyDictionary<string,string> Render(Swarm s); }` returning relative path (`.claude/agents/<role>.md`, forward slashes) -> content for each LLM role. Content: front-matter with `name`, `description` (always double-quoted, escaped), `model`, `tools` (comma-joined), optional `maxTurns`, `effort`, `isolation`, then the prompt; if `Context == "distilled"` append the line `You start with NO prior conversation: everything you know is in the distilled hand-off you were given.`. LF only, trailing newline.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void RendersOneFilePerLlmRole()
{
    var files = AgentFileRenderer.Render(Sample());
    Assert.Equal(new[] { ".claude/agents/expert.md", ".claude/agents/reviewer.md", ".claude/agents/worker.md" }, files.Keys.OrderBy(k => k));
}
[Fact] public void FrontMatterIsValid_AndOnlyDocumentedKeys()
{
    var allowed = new HashSet<string> { "name","description","model","tools","disallowedTools","maxTurns","effort","isolation","permissionMode","skills","background","color" };
    foreach (var (_, text) in AgentFileRenderer.Render(Sample()))
    {
        var lines = text.Split('\n'); Assert.Equal("---", lines[0]);
        var end = Array.IndexOf(lines, "---", 1); Assert.True(end > 0);
        foreach (var l in lines[1..end]) Assert.Contains(l.Split(':')[0], allowed);
    }
}
[Fact] public void OutputIsLfOnly() => Assert.All(AgentFileRenderer.Render(Sample()).Values, t => Assert.DoesNotContain('\r', t));
[Fact] public void DistilledContextAddsNoPriorConversationNote() =>
    Assert.Contains("NO prior conversation", AgentFileRenderer.Render(Sample())[".claude/agents/expert.md"]);
[Fact] public void DescriptionWithQuotesIsEscaped() { /* build a swarm whose worker description is: He said "go" */ }
```

  (`Sample()` = `MarkdownFrontEnd.Parse` of the sample; complete the last test with a builder-made swarm and assert the file contains `description: "He said \"go\""`.)

- [ ] **Steps 2-5:** run (FAIL), implement with a `StringBuilder` using `\n` explicitly, run (PASS), commit `git commit -m "Add agent-file renderer"`.

### Task 7: Workflow renderer and tool-steps runbook

**Files:** Create `src/Swarm.Render/WorkflowRenderer.cs`; Test `tests/Swarm.Tests/WorkflowRendererTests.cs`.

**Interfaces:** Produces `static class WorkflowRenderer { public static IReadOnlyDictionary<string,string> Render(Swarm s); }` returning `.claude/workflows/<name>.js` and `.claude/workflows/<name>.steps.md`. Script rules (each fixes a spike finding):
1. `export const meta = {...}` is a pure literal; phase titles are the stage names without punctuation (`worker`, `gate batch-green`, `reviewer`).
2. Contains **no** `dnx` and no shell command text; only `agent()` calls for Fanout/Role stages. Tool stages become runbook steps; Gate stages read evidence passed in `args.gates["<name>"]` (`{ green: boolean, summary: string }`) with no agent call; if the evidence is absent or red the script sets `halted = true`, logs why, and returns.
3. After the fan-out stage (with escalation `status === 'blocked'` -> escalate-to role), keep only `status === 'done'` results as `state`; collect the rest in `unresolved` and include them in the returned object. If `state` is empty, halt.
4. Every `agent()` call uses `agentType: "<role>"`, `model: "<alias>"` as defined, and a prompt that references the role's task input (reviewer prompt: `Review the work for these completed tasks: ${JSON.stringify(state)}. Inspect each branch's diff.`).
5. LF only; ASCII only.
`<name>.steps.md` (runbook for the main session/CI) lists, in flow order, each Tool stage as a fenced command: `dnx <package>@<version> -- <args>` (never `--yes`), each Gate with its tool and the evidence JSON path it must produce, and a header line `Run these between workflow phases; pass gate results to the workflow as args.gates.`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void ScriptHasNoShellCommands() { var js = Js(); Assert.DoesNotContain("dnx", js); Assert.DoesNotContain("--yes", js); }
[Fact] public void ScriptIsLfAsciiOnly() { var js = Js(); Assert.DoesNotContain('\r', js); Assert.All(js, c => Assert.True(c == '\n' || (c >= ' ' && c < 127))); }
[Fact] public void MetaIsPureLiteralAndFirst() => Assert.StartsWith("export const meta = {", Js());
[Fact] public void ScriptFiltersOnlyDoneResults() => Assert.Contains("status === 'done'", Js());
[Fact] public void ScriptReadsGateEvidenceFromArgs() => Assert.Contains("args?.gates?.[\"batch-green\"]", Js());
[Fact] public void ScriptReturnsUnresolved() => Assert.Contains("unresolved", Js());
[Fact] public void RunbookUsesPinnedPackageAndNoYes()
{
    var md = WorkflowRenderer.Render(Sample())[".claude/workflows/epic-delivery.steps.md"];
    Assert.Contains("dnx Swarm.Squash@0.1.0", md); Assert.DoesNotContain("--yes", md); Assert.DoesNotContain('\r', md);
}
[Fact] public void ScriptPassesNodeSyntaxCheck()
{   // skipped when node is absent
    var node = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Any(p => File.Exists(Path.Combine(p, "node.exe")) || File.Exists(Path.Combine(p, "node")));
    if (!node) return;
    var tmp = Path.Combine(Path.GetTempPath(), $"swarm-{Guid.NewGuid():N}.mjs"); File.WriteAllText(tmp, Js());
    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node", $"--check \"{tmp}\"") { RedirectStandardError = true, UseShellExecute = false })!;
    p.WaitForExit(); File.Delete(tmp); Assert.Equal(0, p.ExitCode);
}
```

  (`Js()` renders the sample and returns the `.js` file; the script uses top-level `await`/`args`/`agent`, so `node --check` on `.mjs` suffices for syntax.)

- [ ] **Steps 2-5:** run (FAIL); implement (start from the spike's `Renderers` workflow generator, apply the five rules above); run (PASS); commit `git commit -m "Add workflow renderer and tool-steps runbook"`.

### Task 8: CLI, packaging, and `dnx` end-to-end

**Files:** Modify `src/Swarm.Cli/Program.cs`; Test `tests/Swarm.Tests/CliTests.cs`.

**Interfaces:**
- Consumes: front-ends and renderers.
- Produces: `swarm validate <file>` (exit 0 prints `ok`; invalid: exit 1, ONE line `error: <message>` on stderr), `swarm render <file> --out <dir>` (format chosen by extension `.md`, `.yaml`/`.yml`; writes agent files, workflow and runbook, creating directories; on any validation error writes NOTHING). Exit codes: 0 ok, 1 invalid definition, 2 usage/IO error. Expose `public static int Run(string[] args, TextWriter stdout, TextWriter stderr)` for testing; `Main` calls it.

- [ ] **Step 1: Tests** for: `validate` ok; `validate` bad model returns 1 with exactly one stderr line and no extra output; `render` writes 5 files (3 agents + js + steps.md) into a temp dir; `render` of an invalid file writes zero files; unknown extension returns 2.
- [ ] **Step 2: Run -> FAIL; Step 3: implement; Step 4: run -> PASS.**
- [ ] **Step 5: Pack and run as a real `dnx` tool from a local feed**

```bash
cd C:/Development/agent-swarm/src/Swarm.Cli && dotnet pack -c Release -o C:/Development/agent-swarm-wt/feed
cd C:/Development/agent-swarm && dnx.cmd Swarm.Cli --version 0.1.0 --add-source C:/Development/agent-swarm-wt/feed -- validate tests/Swarm.Tests/Samples/epic-delivery.md
```

  Expected: prints `ok`, exit 0. Record whether `dnx` prompts (probe 0: it should not) and the first-run time. If `--add-source` is not honoured by `dnx` in this form, record the working invocation.
- [ ] **Step 6: Commit** `git commit -m "Add swarm CLI and dnx packaging"`.

### Task 9: Docs and sweeper

**Files:** Create `docs/definition-format.md`; Modify `README.md`, `AGENTS.md`.

- [ ] **Step 1:** Write `docs/definition-format.md` with `created`/`updated` front-matter: the Markdown grammar, the YAML shape, the C# builder snippet, validation rules and their error messages, the render outputs, the runbook concept (why tool steps are outside the workflow: scripts cannot exec; an LLM gate costs ~31k tokens), and the requirement line `.NET 10+`.
- [ ] **Step 2:** Link it from `README.md` and `AGENTS.md`.
- [ ] **Step 3: Verify the sweeper is clean**

Run: `cd C:/Development/agent-swarm/spikes/04-doc-sweeper/a && dotnet run sweep.cs -- ../../../docs --today 2026-10-03`
Expected: `0 errors`.
- [ ] **Step 4: Commit** `git commit -m "Document the definition format"`.

---

## Self-review

- **Spec coverage:** canonical model + three front-ends (Tasks 2-5), agent files + Workflow script (6-7), `dnx` packaging with explicit invocation (8), docs lifecycle compliance (9). Out of scope here: `swarm run` interpreter, Codex/Gemini renderers, the batch/testgate tools (separate plan), sign-off hook generation (separate plan).
- **Spike findings addressed:** CRLF (Tasks 3, 6, 7 tests), `--yes` (constraint + Task 7 tests), bare package ids (Task 2 validation), blocked-flows-on (Task 7 rule 3), tool steps as LLM calls (Task 7 runbook), thin reviewer prompt (Task 7 rule 4); agent files needing session start: documented in Task 9 (generated files must be installed before the session starts).
- **Placeholders:** the YAML/Markdown parser internals are ported from named spike code and specified by tests and grammar; the final `PackageId` is flagged as a human decision.
- **Type consistency:** `Stage(StageType, string)`, `ToolDef`, `Gate.Tool` and the `FlowParser` are used identically in Tasks 2-7.
