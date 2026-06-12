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
| `src/CelMap.App` | net9.0 | WPF app (begins at Tracer 3). |
| `tests/CelMap.Core.Tests` | net10.0 | xUnit tests (engine). |

> .NET 10 SDK is installed; the WPF template has no `net10.0-windows`, so `CelMap.App`
> targets `net9.0` for now.

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

## Build & test

```sh
dotnet test tests/CelMap.Core.Tests          # run the engine tests
dotnet run --project src/CelMap.Cli -- <source.xlsx> <target.xlsx> \
    [srcSheet] [tgtSheet] [srcHeaderRow=1] [tgtHeaderRow=1] [threshold=80]
```

Output is written to `%USERPROFILE%/Documents/CelMap/<target> (mapped).xlsx`.
Close the target in Excel first — a file open in Excel is locked (friendly handling
comes in Tracer 3).

### Regenerating the alias seed

`synonyms.json` is generated from `UAT Files/generictable.xlsx` by the converter in
`test-fixtures/CreateFixtures` (run it after editing the table).

## Roadmap — v1.5 priorities (hypothetical)

- **Legacy `.xls` support** — read older binary workbooks out of scope for v1.
- **Multi-sheet mapping** — map several source/target sheet pairs in one run.
- **Headless CLI/batch mode** — drive the core engine for automated pipelines.
- **Mapping templates library** — share and reuse saved profiles across users.
