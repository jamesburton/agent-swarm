> THROWAWAY SPIKE 2B: thin pass-through renderer (no typed model)

Run: `dotnet run render.cs -- ../epic-delivery.md <outDir>`; checks: `pwsh ./test.ps1`.

## Metrics

| Metric | Result | Evidence |
|---|---|---|
| Example renders | yes, 3 agent files + 1 workflow | verified (ran) |
| Agent front-matter valid, keys in documented list | yes for expert, reviewer, worker | verified (parsed with a regex check; allowlist from docs/capabilities.md s1, itself *documented* not yet loaded by Claude Code) |
| Workflow passes `node --check` | yes (node v24.19.0) | verified. Not run in the Workflow tool, so API use (`pipeline`, `agent` opts `agentType`/`phase`/`model`) is unverified beyond the authoring reference |
| Invalid input rejected cleanly (exit 1, one line, no files) | 5/5 variants: unknown model, duplicate role, missing model, missing orchestrator section, extra/unrecognised heading | verified via `test.ps1` |
| Lines of code | `render.cs` 115 lines (109 non-blank/non-comment); `test.ps1` 24 | verified |
| Effort to add a second format | see below | assessed |

## What the design is

No model types beyond a private `Sec` bag (title, name, kind, key->value dictionary, body). Validation is inline checks on that bag; agent files are string-built from it; the workflow is one interpolated template with a data array `STAGES` and a single generic runner. All output is accumulated in a dictionary and only written after every check passed, which is what gives "no partial emit".

## Known limits (honest)

- `context: distilled` and `escalate-to: expert` are accepted (swarm-only keys) but currently dropped: nothing in the workflow uses them. In a typed model they would be first-class and a renderer could wire the escalation; here each such feature means more ad hoc code in the template.
- Front-matter values are copied verbatim as strings (no type checks: `maxTurns: abc` passes; `tools` is not split). Only `model` is validated by value.
- Gate semantics are one prompt string ("run the gate"); the workflow does not read evidence.
- Stage wiring is linear only (`stages:` list); no per-stage parallel/fan-out beyond `pipeline` over tickets. `parallel()` is not emitted.
- Parsing is line-based: any body line starting `word: text` directly after the heading is read as a key.

## Adding a YAML or C# front-end

The Markdown parser is not separable from the renderer: validation and emission both read `Sec` (which is really "the Markdown section shape": heading kind, front-matter, body). A second format has two options:

1. **Produce the same `Sec` list** (cheap, ~30-40 lines for YAML with a YAML library; file-based apps can `#:package YamlDotNet`). Roles become `Sec{Name, Kind=model, Keys, Lines=[prompt]}`, the orchestrator `Sec{Kind="code", Keys["stages"]="a, b"}`, gates `Kind="gate"`. This works because `Sec` happens to be a loose dictionary, but then `Sec` *is* a canonical model, an untyped one: every renderer rule that assumes Markdown quirks (comma-joined `stages` string, `Kind` overloaded as model/gate/code, heading-less roles marked `Kind=""`) leaks into the YAML mapper. Error messages also quote Markdown syntax (`'## name  (model)'`), wrong for YAML.
2. **Convert YAML to Markdown text first** and reuse the parser (about 15 lines) - quick but silly, and error locations would point at generated text.

A C# front-end is the same problem plus: C# definitions are code, so they would either emit Markdown/`Sec` (option 2) or need the model to exist as a type. Estimated: YAML ~40 lines and 1 hour, C# ~60-80 lines and needs the `Sec` shape promoted to a real type anyway. Net: the pass-through design is the smallest for one format and loses its advantage at the second, where the typed model (variant A) is what you end up extracting. Not prototyped.

## Verdict

works (for a single Markdown front-end). 115 lines, all four required invalid cases plus a fifth behave. The trade-off against a typed model is front-end extensibility and first-class swarm-only features (`escalate-to`, `context`, gates), not correctness of the example.
