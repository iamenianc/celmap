# Excel Column Mapper – Tracer Bullet Implementation Plan

**Version:** 1.0
**Date:** June 2026
**Companion to:** [EXCEL_MAPPER_PRD.md](EXCEL_MAPPER_PRD.md)

---

## What is a tracer bullet?

A tracer bullet is a *thin, end-to-end slice* of the system that runs from one
end to the other and produces a visible result. Unlike a prototype (which is
throwaway), tracer bullet code is **production code that stays** — each tracer
adds working capability and is built on the last. We "fire" a tracer, see where
it lands, and adjust aim. Every tracer below is independently runnable and
demonstrable.

The five tracers below take us from "can we read two Excel files at all" to a
full WPF app that maps, writes, and reports — each one a working program end to
end, never a horizontal layer in isolation.

---

## Tracer overview

| # | Tracer | End-to-end proof | Primary PRD coverage |
|---|--------|------------------|----------------------|
| 1 | **Read → Write skeleton** | Open source + target `.xlsx`, copy verbatim into a copy of target, save to output dir | §2.1, §2.4, §2.5 (copy/preserve), §9 |
| 2 | **Header + fuzzy match (console)** | Identify headers, fuzzy-match columns, print scored mapping | §2.2, §2.3 (match/score/threshold), §6 |
| 3 | **WPF shell wired to engine** | Click files in a window, run a real map, see output written | §3, §9 (UI/core split), §10 |
| 4 | **Interactive mapping grid + overrides** | Adjust mappings, set thresholds, hide columns, choose write mode | §2.3 (override/ambiguity/hide), §2.5 (modes) |
| 5 | **Profiles, settings, reporting, limits** | Save/load profiles, persist settings, summary report, 100K guard | §2.3 (profiles), §5, §6 (limit), §7 |

---

## Tracer 1 — Read/Write Skeleton (the structural spike)

**Goal:** Prove the riskiest end-to-end path: read a source `.xlsx`, open a
target `.xlsx`, write source values verbatim into a *copy* of the target while
preserving the target's formatting, and save the copy to the output directory.
No matching, no UI — just bytes flowing all the way through.

**Build**
- Solution layout (locks in §9 architecture from day one):
  - `CelMap.Core` — class library, targets **.NET 8/9**. No UI references.
  - `CelMap.Cli` — thin console host that drives `CelMap.Core` for tracers 1–2.
  - `CelMap.App` — WPF EXE (empty until Tracer 3).
  - `CelMap.Core.Tests` — test project.
- Add ClosedXML to `CelMap.Core`.
- `IWorkbookReader` / `WorkbookReader`: open `.xlsx`/`.xlsm`, list sheets, read a
  sheet into an in-memory cell grid preserving cell **value + type** (text vs.
  number vs. date serial) — no conversion (§2.4).
- `ITargetWriter` / `TargetWriter`: copy the target file to the output dir, open
  the copy, write a column of source values verbatim into a hardcoded target
  column, save. Original target is never touched (§2.1, §2.5).
- Output dir resolution: default `%USERPROFILE%\Documents\CelMap`, created if
  missing.

**Demo / "where it lands":** `CelMap.Cli source.xlsx target.xlsx` → produces
`Documents\CelMap\target (mapped).xlsx` with one column copied verbatim and all
original target formatting intact.

**Validates the scary parts:** ClosedXML really does read *and* write while
preserving formatting; verbatim type fidelity holds; copy-not-original works.

---

## Tracer 2 — Header Identification + Fuzzy Matching (console)

**Goal:** Given header-row numbers, build the actual source→target column
mapping with confidence scores. Still console — the matching engine is what we
want to see land.

**Build**
- Add FuzzySharp to `CelMap.Core`.
- `HeaderRow` concept: caller specifies which row holds headers in source and
  target; extract header labels from that row (§2.2).
- `IColumnMatcher` / `ColumnMatcher`:
  - Score every source header against every target header (0–100) (§2.3).
  - Auto-apply matches above a configurable threshold; leave low-confidence
    matches **unset and flagged** (§2.3).
  - Detect **ambiguity**: when two source columns score similarly against one
    target, return both as candidates rather than silently picking (§2.3).
  - Match by **name, not position** (§2.3).
- `MappingResult` model: per-target-column → matched source column (or none),
  score, status (auto / needs-review / ambiguous / unmatched).
- Wire Tracer 1's writer to consume `MappingResult` instead of a hardcoded
  column — now the full **read → match → write verbatim** path runs end to end.
- Warn when a mapped source column is entirely empty (§2.3).

**Demo:** `CelMap.Cli` prints a table — `TargetCol | SourceCol | Score | Status`
— then writes the auto-applied mappings to the output copy.

---

## Tracer 3 — WPF Shell Wired to the Engine

**Goal:** First pixels. A WPF window that lets the user pick files and sheets,
press Run, and get the exact same output Tracer 2 produced from the console —
proving the UI/core split (§9) over a real engine, not a mock.

**Build**
- `CelMap.App` (WPF + CommunityToolkit.Mvvm).
- Main view + `MainViewModel`:
  - Source file / target file pickers (§2.1).
  - Sheet dropdowns populated from `WorkbookReader` (§2.1).
  - Header-row inputs for source and target (§2.2).
  - **Run** command → calls `CelMap.Core` exactly as the CLI did.
  - Status text showing output path on success.
- Optional preload of a default template file (stubbed; fully wired in Tracer 5)
  (§2.1, §5).
- Keep all logic in `CelMap.Core`; the VM only orchestrates. The CLI keeps
  working — two hosts, one engine, proving the boundary holds (§9).

**Demo:** Launch the app, pick two files, pick sheets, set header rows, click
Run → mapped copy appears in the output folder.

---

## Tracer 4 — Interactive Mapping Grid, Overrides & Write Modes

**Goal:** Turn the one-shot Run into a real mapping workflow. The user now *sees*
the scored mappings before writing and steers them.

**Build**
- Mapping grid (DataGrid) bound to `MappingResult`:
  - Column: target name, matched source (editable dropdown), score, status.
  - **Override** any mapping; ambiguous matches show both candidates to choose
    from (§2.3).
  - **Hide/show** columns to cut noise from junk columns (§2.3).
  - Configurable **confidence threshold** slider that re-runs auto-apply (§2.3).
  - Empty-source-column warning surfaced inline (§2.3).
- **Write-mode toggle** (visible UI), per run (§2.5):
  - **Overwrite** (default): write from the row below the target header,
    replacing data rows — with a **confirmation warning** before proceeding.
  - **Append**: write from the first empty row after the last used row.
- Bulk/array writes, not row-by-row (§6).

**Demo:** Run a match, tweak a mapping, hide a junk column, slide the threshold,
pick Append, confirm the overwrite warning on a different run, and verify the
output reflects every choice.

---

## Tracer 5 — Profiles, Settings, Reporting & Safety Limits

**Goal:** Make it sticky, safe, and accountable — the production-hardening
tracer that closes the remaining PRD items.

**Build**
- **Mapping profiles** (§2.3):
  - Save/load profiles as source-name → target-name pairs, **filename-independent**.
  - On a new session, if the same column names appear, auto-load the last
    profile or offer it as a one-click option.
- **Settings persistence** (externalized config file) (§5, §9):
  - Output directory, default template/target, last-used threshold, window prefs.
  - Remember settings between sessions.
- **Reporting** (§7):
  - Post-run summary: columns mapped, rows written, rows/cells skipped and why,
    empty-column warnings — user decides if output is acceptable.
- **Safety limit** (§6): count used source rows below the header; if > **100,000**,
  **stop and warn before processing** (no silent truncation); offer partial run.
- **Output extras** (§2.5): make result available via clipboard/file/integration
  point; values-only (no formulas/source styling); confirm target formatting is
  preserved end to end.
- Keyboard shortcuts for power users (§5).

**Demo:** Save a profile, restart the app, reopen similar files → profile
auto-offered; run a >100K-row file → blocked with a warning; complete a normal
run → full summary report with skip reasons.

---

## Sequencing rationale

1. **Tracer 1 first** because read+write-while-preserving-formatting is the
   single biggest technical risk (it's why the PRD switched libraries off
   `ExcelDataReader`). If ClosedXML couldn't do it, everything else is moot.
2. **Matching (2)** before any UI so the engine is proven headless and testable.
3. **UI shell (3)** only once there's a real engine to wire to — avoids building
   screens against mocks.
4. **Interactivity (4)** layers the human-in-the-loop workflow onto a working
   pipeline.
5. **Persistence/safety/reporting (5)** last — these harden and polish a system
   that already works end to end.

Each tracer leaves `main` in a runnable, demonstrable state.
