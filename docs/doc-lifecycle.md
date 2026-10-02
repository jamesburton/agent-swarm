---
created: 2026-10-02
updated: 2026-10-02
status: current
---
# Self-evolving docs and memories

Docs, notes and memories decay. They should be cheap to load, carry their own age, and be pruned or refined as they are used. **Proposed convention; applies to `docs/`, `.docs/`, and any lasting agent memory.**

## Principles

1. **Light front-matter.** Only what is needed to decide relevance and judge age; everything else is body. Entries are loaded **on demand via references** (an index links to one-screen summaries, which link deeper) rather than all at once.
2. **Timestamps on every lasting entry.** `created` and `updated` (ISO date) are mandatory. Optionally `reviewed` when an entry was checked and found still correct without changing it.
3. **Temporal decay.** Age relative to the entry's type drives status; nothing is trusted forever.
4. **Review on encounter and periodically.** When an entry is read and used, check it against reality:
   - still right → bump `reviewed` (a touch-only change, so relevance is tracked without rewriting);
   - partly wrong → refine the content and bump `updated`;
   - obsolete → archive.
   A periodic sweep does the same for entries nobody has encountered.
5. **Mark, then archive.** Entries getting old or doubtful are marked `[STALE?]` in the index/title so readers treat them with caution; confirmed-obsolete entries move to an archive (kept in git history or `archive/`), and are removed from the index so they stop cluttering loads.
6. **Whittle, don't hoard.** Merge duplicates, delete what the code/repo already records, convert relative dates to absolute.

## Minimal entry shape

```markdown
---
created: 2026-10-02
updated: 2026-10-02
reviewed: 2026-10-02   # optional
status: current        # current | stale? | archived
---
# Title
One-screen summary, then links to deeper pages.
```

## Open questions

- Staleness thresholds per entry type (device facts vs. design decisions vs. benchmark numbers).
- Who sweeps: a scheduled agent, a hook on read, or manual.
- Whether `reviewed` stays optional or `updated` alone suffices.
- Archive location: `archive/` folder vs. git history only.
- Tooling: a `dnx` linter/sweeper that reports `[STALE?]` candidates and index drift.
