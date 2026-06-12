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

| # | Tracer | Status | End-to-end proof | Primary PRD coverage |
|---|--------|--------|------------------|----------------------|
| 1 | **Read → Write skeleton** | ✅ Done | Open source + target `.xlsx`, copy verbatim into a copy of target, save to output dir | §2.1, §2.4, §2.5 (copy/preserve), §9 |
| 2 | **Header + match engine (console)** | ✅ Done | Headers, **tiered** match (qualified/exact/alias/fuzzy), strict groups, org-code aliases, token-gated rules, scored mapping | §2.2, §2.3 (match/score/threshold), §6 |
| 3 | **WPF shell wired to engine** | ⬜ Next | Click files in a window, run a real map, see scored mapping + tier, output written | §3, §9 (UI/core split), §10 |
| 4 | **Interactive mapping grid + overrides** | ⬜ | Adjust mappings, set thresholds, hide columns, choose write mode | §2.3 (override/ambiguity/hide), §2.5 (modes) |
| 5 | **Profiles, settings, rules editor, reporting, limits** | ⬜ | Save/load profiles, edit alias + qualified rules in UI, persist settings, summary report, 100K guard | §2.3 (profiles/rules), §5, §6 (limit), §7 |

> **Note on scope drift (good kind):** Tracer 2 absorbed a full **rules engine** that
> wasn't in the original plan — match tiers, alias/synonym groups (strict vs loose),
> and qualified token-gated rules with synonym-aware qualifiers — driven by real UAT
> data (`generictable.xlsx`) and the insurance domain. This pulls a meaningful chunk of
> §2.3 forward and adds a **rules editor** obligation to Tracer 5. See the Tracer 2
> status note for the full inventory.

---

## Tracer 1 — Read/Write Skeleton (the structural spike)

> **Status: ✅ Done.** Solution scaffolded (`CelMap.Core`/`CelMap.Cli`/`CelMap.App`/
> `CelMap.Core.Tests`). `WorkbookReader` reads `.xlsx`/`.xlsm` into a typed cell grid;
> `TargetWriter` copies the target to the output dir and writes verbatim, leaving the
> original untouched. Verified end to end on the real Dyson UAT file (2 sheets, 22
> columns with multi-line headers, 60 data rows below header row 6) and on fixtures
> with bold-header + column-width preservation confirmed. 8 unit tests passing.
> **Note:** WPF App targets `net9.0` (template has no `net10.0-windows` yet); Core/Cli/
> Tests target `net10.0` (only SDK installed). **Known gap:** ClosedXML throws a raw
> `IOException` if the file is open in Excel — friendly message deferred to a later tracer.

**Goal:** Prove the riskiest end-to-end path: read a source `.xlsx`, open a
target `.xlsx`, write source values verbatim into a *copy* of the target while
preserving the target's formatting, and save the copy to the output directory.
No matching, no UI — just bytes flowing all the way through.

**Build**
- Solution layout (locks in §9 architecture from day one):
  - `CelMap.Core` — class library, **net10.0**. No UI references.
  - `CelMap.Cli` — thin console host (net10.0) that drives `CelMap.Core` for tracers 1–2.
  - `CelMap.App` — WPF EXE, **net9.0** (template lacks net10.0-windows; empty until Tracer 3).
  - `CelMap.Core.Tests` — test project (net10.0).
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

> **Status: ✅ Done — and the matching engine grew well beyond the original plan.**
> FuzzySharp added. `HeaderExtractor` pulls labels from a chosen header row;
> `ColumnMatcher` scores every source against every target 0–100, auto-applies above a
> configurable threshold, flags below-threshold as **NeedsReview**, returns **Ambiguous**
> candidates within a tie margin, warns on empty source columns, and matches by name not
> position. The writer consumes a `MappingResult` — full **read → match → write verbatim**
> path runs end to end. CLI prints the `TargetCol | SourceCol | Score | Status` table.
>
> **Beyond the original Tracer 2 scope (added live during UAT, all in `CelMap.Core`):**
> - **Match tiers** `MatchKind`: **Qualified > Exact > Alias > Fuzzy**. A certainty never
>   loses to a coincidental fuzzy 100; ambiguity is scoped *within* a tier.
> - **Alias rules** (`AliasRules`, `synonyms.json`) — bidirectional synonym groups that
>   short-circuit to score 100. Seeded (29 groups) from `UAT Files/generictable.xlsx`,
>   the org's curated label↔code table. The matcher translates messy client labels into
>   internal **org codes** (e.g. `Date Joined Scheme → DJS`).
> - **Strict vs loose groups** — strict groups (IDs/keys: `MemberID`, `GroupID`,
>   `EmployeeRef`, `DJS`, `ReviewDate`, `TFN`, …) refuse fuzzy fallback: no exact/alias
>   hit ⇒ left **Unmatched** (manual-only), never guessed. Loose groups fall through to
>   fuzzy. Default = loose.
> - **Qualified (token-gated) rules** (`QualifiedRules`, `qualified_rules.json`) for the
>   ambiguous GL/GSC/TPD split fields (`CategoryNo`/`FUL`/`Loading`/`Term`/`Threshold`).
>   A source qualifies only if its header carries the concept **and** a benefit
>   qualifier; bare concept ⇒ forced manual review. Qualifier slots are **synonym-aware**
>   (insurance domain: GL≡Group Life≡Death; GSC≡GIP≡Income Protection≡Salary Continuance;
>   TPD≡Total & Permanent Disability) — 15 rules.
>
> **81 tests passing.** Both rule files are `Content` copied to host output and ship beside
> the CLI. Conversion of `generictable.xlsx` and collision-free merging are guarded by
> tests. **Known gap (carried):** ClosedXML throws a raw `IOException` when a file is open
> in Excel — friendly handling deferred to Tracer 3. **Open question:** qualifier tokens
> match as substrings (so `GLFUL` written solid still hits); revisit word-boundary
> matching if a real collision appears.

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
press Run, **see the scored mapping it produced**, and get the exact same output
Tracer 2 produced from the console — proving the UI/core split (§9) over a real
engine, not a mock. Crash-resistant (§10) from the first window.

**Build**
- `CelMap.App` (WPF + CommunityToolkit.Mvvm).
- Main view + `MainViewModel`:
  - Source file / target file pickers (§2.1).
  - Sheet dropdowns **auto-populated on file pick** via `WorkbookReader`
    (`GetSheetNames` fires when a file is chosen, not behind a separate action) (§2.1).
  - Header-row inputs for source and target, **with a preview of the selected
    header row's values** (or first ~5 rows) so the user can confirm they picked
    the real header — the Dyson UAT file's header was row 6, not row 1, so a blind
    number box is a foot-gun (§2.2).
  - **Run** command → calls `CelMap.Core` exactly as the CLI did. **Async / off the
    UI thread** so a large (up to 100K-row) write never freezes or looks hung (§10).
  - **Read-only scored results display** (DataGrid or list) showing
    `Target | Source | Score | Match (tier) | Status` for the run — the
    `MappingResult` already exists from Tracer 2, so surface it instead of writing
    blind. Show the **match tier** (`Qualified`/`Exact`/`Alias`/`Fuzzy`) so the user
    can see *why* something matched, and visually distinguish the rules-engine
    outcomes that now exist: **strict-unmatched** (manual-only, fuzzy suppressed) and
    **qualified needs-review** (ambiguous concept, e.g. bare `CategoryNo`) read very
    differently from a plain low-score miss. Tracer 4 makes this grid *editable*;
    here it is view-only.
  - Load the default **alias + qualified rule** files (`synonyms.json`,
    `qualified_rules.json`) at startup exactly as the CLI does, so the org-code
    translation and benefit-type disambiguation work in the app from first launch.
  - Status text showing output path on success; a clean "nothing matched / nothing
    to write" status when the run produces zero auto-mappings (no silence, no crash).
- **Error handling from day one (§10 — no crashes on valid input):** wrap engine
  calls so a locked-file `IOException` (file open in Excel — the known Tracer 1 gap)
  and other expected failures surface as a friendly message ("close the file in
  Excel and retry"), never an unhandled exception that kills the window.
- Optional preload of a default template file (stubbed; fully wired in Tracer 5)
  (§2.1, §5).
- Keep all logic in `CelMap.Core`; the VM only orchestrates. The CLI keeps
  working — two hosts, one engine, proving the boundary holds (§9).

**Demo:** Launch the app, pick two files (sheets auto-fill), confirm header rows
against the preview, click Run → scored mapping table appears in the window **and**
a mapped copy appears in the output folder. Re-run with the target file open in
Excel → friendly error, no crash.

---

## Tracer 4 — Interactive Mapping Grid, Overrides & Write Modes

**Goal:** Turn the one-shot Run into a real mapping workflow. The user now *sees*
the scored mappings before writing and steers them.

**Build**
- Mapping grid (DataGrid) bound to `MappingResult`:
  - Column: target name, matched source (editable dropdown), score, **match tier**, status.
  - **Override** any mapping; ambiguous matches show both candidates to choose
    from (§2.3). For a **qualified needs-review** row (bare `CategoryNo` etc.) the
    dropdown should offer the governed targets (GSC/GL/TPD…) as the obvious choices.
  - **Strict** targets that came back Unmatched should be visually flagged as
    "needs manual map" rather than looking like an empty miss — they were
    *deliberately* not guessed.
  - **Hide/show** columns to cut noise from junk columns (§2.3).
  - Configurable **confidence threshold** slider that re-runs auto-apply (§2.3).
    Note the threshold only affects **fuzzy**; tiered certainties (qualified/exact/
    alias = score 100) are unaffected by the slider — make that legible.
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
- **Rules editor (front-end)** (§2.3, §5) — now covers **both** rule files the
  engine grew during Tracers 1–2:
  - **Alias/synonym rules** (`synonyms.json`, seeded from `generictable.xlsx`):
    add/remove synonym groups and members, toggle a group **strict vs loose**.
  - **Qualified (token-gated) rules** (`qualified_rules.json`): add/edit a rule's
    target, its **concept** tokens, and its **requireAll** qualifier slots
    (each slot a set of synonym alternatives — e.g. GSC≡GIP≡Income Protection).
    This is where new GL/GSC/TPD-style split fields get onboarded without code.
  - Persist back to the JSON files; profiles may later layer per-profile rules on top.
  - **Decision to revisit here:** qualifier matching is currently **substring**; if
    UAT surfaces a false positive, add a word-boundary option in this editor.
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
