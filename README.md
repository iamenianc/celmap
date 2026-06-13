# CelMap

Excel Column Mapper — a Windows desktop utility that maps columns between two
arbitrary Excel files and writes the result to a copy of the target file while
preserving its formatting. Values are copied verbatim (no type conversion).

It translates messy real-world client column labels into an organisation's internal
**target codes** using a configurable rules engine, and surfaces a scored,
human-reviewable mapping before writing.

See [EXCEL_MAPPER_PRD.md](EXCEL_MAPPER_PRD.md) for the full product requirements
and [TRACER_BULLETS.md](TRACER_BULLETS.md) for the implementation plan and status.

## Solution layout

| Project | Target | Role |
|---------|--------|------|
| `src/CelMap.Core` | net10.0 | Engine: read/write (ClosedXML), matching (FuzzySharp), rules. No UI. |
| `src/CelMap.Cli` | net10.0 | Console host driving the engine (Tracers 1–2). |
| `src/CelMap.App` | net10.0-windows | WPF app — interactive click-to-map UI (Tracers 3–4). |
| `tests/CelMap.Core.Tests` | net10.0 | xUnit tests (engine). |

## Matching engine

Columns are matched by **name, not position**, in tiers (highest wins, and a
certainty is never beaten by a coincidental fuzzy score):

1. **Qualified** — token-gated domain rules (`src/CelMap.Core/qualified_rules.json`)
   for ambiguous split fields. Synonym-aware (insurance: GL≡Group Life≡Death;
   GSC≡GIP≡Income Protection≡Salary Continuance; TPD≡Total & Permanent Disability).
2. **Exact** — normalised name equality.
3. **Alias** — synonym groups (`src/CelMap.Core/synonyms.json`, seeded from the org's
   `generictable.xlsx`). Groups are **strict** (IDs/keys — never fuzzy-guessed; left
   for manual mapping if no exact/alias hit) or **loose** (fuzzy fallback allowed).
4. **Fuzzy** — token-set ratio above a configurable confidence threshold; below it the
   column is flagged for manual review.

Both rule files are copied beside the host on build and editable by hand (a UI editor
arrives in Tracer 5).

## Run the app

```sh
dotnet run --project src/CelMap.App            # the WPF desktop app
```

The app is a **single-screen staged workflow** (not a wizard):

1. Pick the **source** and **target** files; sheets auto-populate. Set each header row.
2. **① Auto-map** — the engine fills in the matches it's confident about.
3. **Review** — auto-matches sit in their own section; click one to **reject** it.
4. **Map the rest by mouse** — click a source column on the left to pick it up, then click
   its target row on the right to link them. As rows get mapped they move out of the way so
   you can focus on what's left.
5. Choose **Overwrite** (default; replaces data rows below the header, with a confirmation) or
   **Append** (writes after the last used row), then **② Confirm & execute**.

The confidence slider re-runs auto-apply live (it only moves fuzzy matches — tiered
certainties stay put) and keeps your manual overrides. Columns can be hidden to exclude them
from the write.

## Build & test

```sh
dotnet test tests/CelMap.Core.Tests          # run the engine tests
dotnet run --project src/CelMap.Cli -- <source.xlsx> <target.xlsx> \
    [srcSheet] [tgtSheet] [srcHeaderRow=1] [tgtHeaderRow=1] [threshold=80]
```

Output is written to `%USERPROFILE%/Documents/CelMap/<target> (mapped).xlsx` (the original
target is never modified). Close the target in Excel first — a file open in Excel is locked;
the app surfaces this as a friendly "close it in Excel and retry" message instead of crashing.

### Regenerating the alias seed

`synonyms.json` is generated from `UAT Files/generictable.xlsx` by the converter in
`test-fixtures/CreateFixtures` (run it after editing the table).

## Development Guidelines

- **No Node.js / npm Dependencies**: This project is a pure .NET solution (C# / WPF) with native Windows utilities (PowerShell). Do not introduce Node.js, npm, or Python dependencies to any part of the codebase or helper tools.

## Roadmap — v1.5 priorities (hypothetical)

- **Legacy `.xls` support** — read older binary workbooks out of scope for v1.
- **Multi-sheet mapping** — map several source/target sheet pairs in one run.
- **Batch/automated runs** — the CLI already drives the engine headlessly; extend it to
  apply a saved profile across many files unattended (profiles land in Tracer 5).
- **Mapping templates library** — share and reuse saved profiles across users.
