# Spike 2A: typed canonical model renderer

> THROWAWAY SPIKE. Run: `dotnet run render.cs -- epic-delivery.md <outDir>` (or `epic-delivery.yaml`); all checks: `pwsh run-tests.ps1`; parse outputs: `dotnet run render.cs -- --check <outDir>`.

| Metric | Result |
|---|---|
| Example renders | yes: 3 agent files + 1 workflow (md and yaml give identical output) |
| Agent frontmatter valid | yes: parsed, fields within name/description/model/tools/maxTurns/effort/isolation |
| `node --check` workflow | pass (node 24.19.0) |
| Invalid input (exit 1, one line, 0 files) | unknown model alias, duplicate role, missing `model`, missing orchestrator section, extra heading: all rejected |
| Lines of code (render.cs, 322 total) | model+validation ~70, md front-end ~66, yaml front-end ~40, renderers ~105 |
| Effort to add YAML | 3 steps (parser, one switch arm, example file); no model changes |

Error samples: `error: unknown model alias 'gpt5' in role 'worker'`, `error: duplicate name 'worker'`, `error: extra section: unrecognised heading '## Notes' ...`.

## Top caveats
1. Workflow script only syntax-checked; never executed by the Workflow tool. API usage follows docs (unverified at runtime), and results are only logged, not returned.
2. YAML front-end is a stub subset parser, not real YAML.
3. "Missing/extra section" semantics are my interpretation; the example definition text was authored here, and prose lines shaped like `word: x` in a section are parsed as keys.
