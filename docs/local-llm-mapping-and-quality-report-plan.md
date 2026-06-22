# Plan: Local-only LLM mapping assist + unmapped-source quality report for CelMap

## Context

CelMap is a .NET 10 desktop/CLI tool that maps columns between two Excel files using a
deterministic, tiered engine (Qualified > Exact > Alias > Fuzzy) and copies cell values
verbatim. Two gaps remain after a run: (1) some target columns stay
`NeedsReview`/`Ambiguous`/`Unmatched` and need manual resolution, and (2) **source** columns
that never got mapped are simply dropped with no insight into *why* or whether they hid data
problems.

This work adds two cooperating, **fully local / offline** features:

- **A. Local-LLM mapping assist** — an opt-in fallback that, only for low-confidence
  targets, asks a locally-run quantized model (via Ollama, e.g. `phi4-mini`/an 8B GGUF) to
  propose a source column. Deterministic results stay authoritative.
- **B. Unmapped-source quality report** — for every source column the engine did **not**
  map, compute *easy, deterministic* data-quality findings (duplication, missing data,
  inconsistencies), optionally have the local model write a plain-English summary, and
  surface it as a **WPF panel** and as an **extra worksheet in the output file**.

**Hard constraint (per user):** nothing connects to any external/cloud server. The *only*
network activity permitted is the **one-time model download** (`ollama pull …`). At runtime
everything talks at most to a local Ollama instance on `localhost`; with Ollama absent,
both features degrade gracefully and the rest of CelMap works exactly as today. The
deterministic quality report needs no model at all and is always available offline.

Decisions confirmed with the user: quality findings are **deterministic + optional LLM
summary**; **keep both** the mapping assist and the report; surface the report in the **WPF
panel** and an **extra output sheet**.

---

## Part A — Local-LLM mapping assist (fallback for low-confidence targets)

A decorator `LlmAssistedColumnMatcher : IColumnMatcher` wraps `ColumnMatcher`:

1. Run the deterministic matcher first.
2. If `options.LlmAssistEnabled` is false (or Ollama unreachable / nothing unresolved),
   return the deterministic result unchanged.
3. Collect targets whose status is not `Auto`, and source columns **not** already claimed by
   a deterministic `Auto` match.
4. Send **one batched** few-shot request to local Ollama (`/api/chat`, `format=json`,
   `stream=false`, `temperature=0`) listing unresolved targets + candidate sources with a
   few sample values each; expect a JSON array of `{target, source|null, confidence}`.
5. Apply high-confidence suggestions: rewrite those targets to `MatchStatus.Auto` with
   `MatchKind.Llm`, enforcing one-source-per-target (mirrors the existing
   `SuppressFuzzyReuseOfClaimedSources` claim rule in `ColumnMatcher.cs`).
6. **Any** failure is swallowed → deterministic result returned. Never throws, never blocks.

### Files (Part A)
- New `src/CelMap.Core/Llm/IOllamaClient.cs` — `Task<string?> ChatJsonAsync(system, user, ct)`
  (interface enables unit tests with no live server).
- New `src/CelMap.Core/Llm/OllamaClient.cs` — `HttpClient` to a configurable `localhost`
  endpoint, using **built-in** `System.Net.Http` + `System.Text.Json` (no new NuGet dep).
- New `src/CelMap.Core/Llm/LlmConfig.cs` — `record LlmConfig(bool Enabled, string Endpoint,
  string Model, double Temperature, int TimeoutSeconds, double MinConfidence)` with
  `LoadDefault()` reading `llm_config.json` from `AppContext.BaseDirectory` (same pattern as
  `AliasRules.LoadDefault`), plus env-var overrides (`OLLAMA_HOST`, `CELMAP_LLM_MODEL`,
  `CELMAP_LLM_ENABLED`). Defaults: `http://localhost:11434`, **`phi4-mini` (3.8B, Q4_K_M)**,
  temp `0`, 30s, min-confidence `0.8`, **disabled by default**. See "Recommended model" below.
- New `src/CelMap.Core/Llm/LlmAssistedColumnMatcher.cs` — the decorator + few-shot prompt
  builder. System prompt states the task + output contract + worked column-name examples
  (adapted from the user's ADP→Workday example, e.g. `Emp_ID`→`employee_id`); instructs the
  model to use only the provided sources and emit `null` when none fit.
- New `src/CelMap.Core/llm_config.json` — shipped default config.
- Modify `src/CelMap.Core/MappingResult.cs` — add `Llm` to `MatchKind`
  (`Fuzzy=0, Alias=1, Exact=2, Qualified=3, Llm=4`); value is cosmetic (decorator-only) so
  the UI/CLI can badge AI matches.
- Modify `src/CelMap.Core/IColumnMatcher.cs` — add `bool LlmAssistEnabled = false` to
  `MatcherOptions`; add optional `Func<HeaderColumn, IReadOnlyList<string>>? sourceSamples
  = null` to `Match` (keeps `ColumnMatcher` and all existing callers compiling; it ignores it).
- Modify `src/CelMap.Core/CelMap.Core.csproj` — add `Content Include="llm_config.json"
  CopyToOutputDirectory="PreserveNewest"` alongside the existing `synonyms.json` entry.

### Recommended model (as of 2026-06)
This task is light — small JSON mapping objects + a short quality summary on a no-GPU CPU —
so favor a strong instruction-follower with reliable constrained-JSON output over a heavy
reasoner. Validated against current local-LLM rankings:
- **Default: `phi4-mini` (3.8B) @ `Q4_K_M`** — ~2.3 GB, ~12 tok/s CPU-only, MIT, 128K ctx,
  class-leading instruction following; JSON locked via Ollama `format`.
- **Quality option: `qwen3:8b` @ `Q4_K_M`** — ~5 GB, strongest small model for structured
  JSON / messy-header disambiguation, Apache-2.0; slower on CPU but fine for batched calls.
- **Quantization:** `Q4_K_M` (≈92% quality at ≈70% smaller). Configurable via `llm_config.json`
  / `CELMAP_LLM_MODEL`. One-time `ollama pull phi4-mini`, then fully offline.

---

## Part B — Unmapped-source quality report (deterministic; optional LLM summary)

### B1. Profiling engine (deterministic, Core, offline)
Add `src/CelMap.Core/Quality/SourceQualityProfiler.cs` producing a
`SourceQualityReport(IReadOnlyList<ColumnQuality>)`, where
`ColumnQuality(HeaderColumn Column, int TotalRows, int BlankCount, double BlankPct,
int DistinctCount, int DuplicateRowCount, IReadOnlyList<(string Value,int Count)>
TopDuplicates, IReadOnlyList<string> Inconsistencies, IReadOnlyList<string> SampleValues)`.

Computed over each **unmapped source column** (source columns not present as keys in
`MappingResult.ToColumnMap()`), reading only the data rows below the header — reuse/extend
`SheetDataExtensions` (which already has `ColumnIsEmpty`/`PopulatedRowSpan`) and
`CellValue.Type` (`Empty/Text/Number/Boolean/DateTime/Error`). "Easy to compute" checks:
- **Missing data:** blank/`Empty` count and percentage.
- **Duplication:** distinct vs. total; top repeated values with counts.
- **Inconsistencies (cheap heuristics):** mixed `CellValueType` in one column
  (e.g. Number + Text); mixed date formats / numbers stored as text; inconsistent
  casing/leading-trailing whitespace; mixed units/currency symbols via simple regex. Each
  finding is a short human-readable string — no model required.

A helper `SourceQualityProfiler.Profile(SheetData source, IReadOnlyList<HeaderColumn>
sourceHeaders, IReadOnlyDictionary<int,int> appliedColumnMap, int srcHeaderRow)` returns the
report; this is the single entry point both the CLI and App call.

### B2. Optional LLM summary (local, offline, opt-in)
Add `src/CelMap.Core/Quality/QualitySummarizer.cs` that, when `LlmConfig.Enabled`, feeds the
**already-computed** deterministic findings (not raw data) to the local `IOllamaClient` and
asks for a short plain-English narrative ("3 of 5 unmapped columns have >20% blanks; column
'Sal' mixes text and numbers …"). On any failure it returns the deterministic findings with
no narrative. Reuses the Part A `IOllamaClient`/`LlmConfig` — no second model, still the only
network use is the one-time pull.

### B3. Output: extra worksheet in the mapped file
Extend the writer to append a `CelMap Quality Report` worksheet to the output workbook.
Cleanest seam: `TargetWriter.Write` already has the `XLWorkbook wb` open before `wb.Save()`
(`src/CelMap.Core/TargetWriter.cs:154`). Add an optional `SourceQualityReport? QualityReport`
to `WriteRequest`; when present, add a sheet via `wb.AddWorksheet(...)` and write one row per
unmapped column (label, total, blanks, blank %, distinct, duplicate rows, top duplicates,
inconsistencies, and the optional LLM narrative in a header block) before `wb.Save()`. No new
file is created and the existing data-writing path is untouched.

### B4. Surfaces
- **WPF panel:** after a match, compute the report (deterministic, on the cached
  `_sourceData`/`_sourceHeaders` in `MainViewModel`) and bind it to a new collapsible panel
  in the mapping screen showing the unmapped columns and their findings, so the user reviews
  issues before Execute. The optional LLM summary populates a header text block when the AI
  toggle is on. Pass the report into `WriteRequest` on Execute so it also lands in the file.
- **Output sheet:** as in B3, travels with the result for both App and CLI runs.
- (CLI gets the extra sheet for free via the writer; a console table is out of scope per the
  user's surface choices.)

### Files (Part B)
- New `src/CelMap.Core/Quality/SourceQualityProfiler.cs`, `.../ColumnQuality.cs` (+ report
  record), `src/CelMap.Core/Quality/QualitySummarizer.cs`.
- Modify `src/CelMap.Core/SheetDataExtensions.cs` — add small profiling helpers if useful
  (distinct/blank counts per column over the data range).
- Modify `src/CelMap.Core/TargetWriter.cs` + the `WriteRequest` record — optional
  `QualityReport`; append the report worksheet before `wb.Save()`.
- Modify `src/CelMap.App/MainViewModel.cs` — compute the report in `RunMatchAsync` (reuse
  cached `_sourceData`, `_sourceHeaders`, `result.ToColumnMap()`), expose it as an
  `[ObservableProperty]`, and include it in the `WriteRequest` built in `WriteAsync`.
- New WPF view/section + a `MappingViewModel`/`MainViewModel` binding for the quality panel
  (follow existing Material Design patterns in the mapping view).

---

## Shared wiring
- `src/CelMap.App/MainViewModel.cs`: construct `LlmAssistedColumnMatcher` in the ctor
  (line ~49); add `[ObservableProperty] bool _llmAssistEnabled` near `_fuzzyEnabled`
  (~line 128); thread `LlmAssistEnabled` + `sourceSamples` (reuse `_sourceSamples`,
  line 281/35) into the two `MatcherOptions`/`Match` calls (~lines 266, 401). The LLM call
  runs inside the existing `Task.Run`, so the decorator may block on the async client
  (`.GetAwaiter().GetResult()`) without freezing the UI.
- `src/CelMap.Cli/Program.cs`: add `--llm` (and `--llm-model <name>`) flags; load
  `LlmConfig`; build a `sourceSamples` closure from `sourceData`; the quality report is
  always computed and written into the output workbook.
- `src/CelMap.App` XAML: add a "Use local AI assist (Ollama)" checkbox bound to
  `LlmAssistEnabled` next to the fuzzy toggles; teach the mapping grid badge to recognize
  `MatchKind.Llm`; add the quality-report panel.
- `README.md`: document both features, the strict local-only behavior (Ollama on localhost;
  one-time `ollama pull phi4-mini`; works fully offline thereafter), the `--llm` flag/app
  toggle, and the `llm_config.json` keys + env-var overrides.

`IColumnMatcher.Match` stays synchronous (both consumers already call it inside `Task.Run`),
avoiding cascading async signature changes.

---

## Verification

1. **Unit tests** (`dotnet test tests/CelMap.Core.Tests`, no live server):
   - *Profiler:* a column with blanks → correct blank count/%; repeated values → duplicate
     counts + top-duplicates; mixed `CellValueType` / mixed date formats → inconsistency
     strings; only **unmapped** columns appear in the report; clean column → no findings.
   - *Mapping assist (fake `IOllamaClient`):* suggestion fills an `Unmatched` target →
     `Auto` + `MatchKind.Llm`; a deterministically-claimed source is never reassigned; two
     suggestions on one source → one wins; client throws / null / invalid JSON →
     result identical to deterministic-only; `LlmAssistEnabled=false` → client never called.
   - *Summarizer:* client failure → findings returned with no narrative (no throw).
   - *Writer:* `WriteRequest` with a `QualityReport` → output workbook contains the
     `CelMap Quality Report` sheet with the expected rows; without it → unchanged behavior.
2. **Existing suite stays green** — confirms the optional `Match` param, new enum value, and
   `WriteRequest` change don't regress `ColumnMatcherTests`, `MatchWriteIntegrationTests`,
   `TargetWriterTests`, etc.
3. **Manual end-to-end (no-GPU machine):**
   - Offline, no Ollama: run CLI/App on a sheet with unmapped columns containing blanks &
     dupes → confirm the quality sheet/panel appears with correct deterministic findings and
     no crash; `--llm` simply no-ops with a warning.
   - With Ollama (`ollama pull phi4-mini && ollama serve`): `dotnet run --project
     src/CelMap.Cli -- source.xlsx target.xlsx --llm` → confirm low-confidence targets get
     AI suggestions (badged) and the quality sheet includes the AI narrative; then stop
     `ollama serve` and rerun → still completes deterministically.
   - WPF: toggle "Use local AI assist", re-match → AI rows badged & reviewable, quality
     panel populated, and the report present in the written file after Execute.
