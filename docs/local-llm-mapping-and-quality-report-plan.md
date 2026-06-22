# Plan: Local-LLM mapping assist + analyst data-quality report (LLM-authored, template-steered)

## Context

CelMap is a .NET 10 desktop/CLI tool that maps columns between two Excel files using a
deterministic, tiered engine (Qualified > Exact > Alias > Fuzzy) and copies cell values
verbatim. Two needs go beyond mapping:

1. After a run, some target columns stay `NeedsReview`/`Ambiguous`/`Unmatched` and need manual
   resolution.
2. A human **analyst** must inspect the client's source data for quality problems (missing
   values, duplication, inconsistencies) and decide what to ask the client. Today this is
   manual and inconsistent.

This work delivers, **fully local / offline**:

- **A. Local-LLM mapping assist** — an opt-in fallback that, only for low-confidence target
  columns, asks a locally-run quantized model (Ollama) to propose a source column.
  Deterministic results stay authoritative.
- **B. Analyst data-quality report (primary deliverable)** — deterministically detect
  data-quality issues across **all source columns**, then have the local model write a
  **comprehensive, human-readable report** that explains the findings and **suggests questions
  the analyst could ask the client**. The output is an **internal aid for the analyst**, not a
  client-facing document — the analyst reads it and writes their own client communication. The
  report is a **standalone Markdown file** saved next to the mapped workbook.

**Developer steerability (new requirement):** the developer steers the LLM with **templates
of past human-written reports**. Two inputs: a **structural template** that defines the
report's sections, and a **folder of example reports** used as few-shot exemplars for tone,
depth, and the kinds of questions analysts ask. Both are plain files the developer edits.

**Hard constraint:** nothing connects to any external/cloud server. The only network activity
permitted is the **one-time model download** (`ollama pull …`). At runtime everything talks at
most to a local Ollama instance on `localhost`; with Ollama absent, the report falls back to a
deterministic template-filled version and everything else still works.

Decisions confirmed with the user: report covers **all source columns with issues**; output is
a **standalone Markdown report**; steering is via **a structural template + a folder of example
reports**; the **LLM mapping assist (Part A) is kept**.

---

## Part A — Local-LLM mapping assist (fallback for low-confidence targets)

A decorator `LlmAssistedColumnMatcher : IColumnMatcher` wraps `ColumnMatcher`:

1. Run the deterministic matcher first.
2. If `options.LlmAssistEnabled` is false (or Ollama unreachable / nothing unresolved),
   return the deterministic result unchanged.
3. Collect targets whose status is not `Auto`, and source columns **not** already claimed by a
   deterministic `Auto` match.
4. Send **one batched** few-shot request to local Ollama (`/api/chat`, `format=json`,
   `stream=false`, `temperature=0`) listing unresolved targets + candidate sources with a few
   sample values each; expect a JSON array of `{target, source|null, confidence}`.
5. Apply high-confidence suggestions: rewrite those targets to `MatchStatus.Auto` with
   `MatchKind.Llm`, enforcing one-source-per-target (mirrors `SuppressFuzzyReuseOfClaimedSources`
   in `ColumnMatcher.cs`).
6. **Any** failure is swallowed → deterministic result returned. Never throws, never blocks.

### Files (Part A)
- New `src/CelMap.Core/Llm/IOllamaClient.cs` — `Task<string?> ChatJsonAsync(system, user, ct)`
  and `Task<string?> ChatTextAsync(system, user, ct)` (interface enables tests with no server;
  the text variant is what Part B uses for the report prose).
- New `src/CelMap.Core/Llm/OllamaClient.cs` — `HttpClient` to a configurable `localhost`
  endpoint, **built-in** `System.Net.Http` + `System.Text.Json` (no new NuGet dep).
- New `src/CelMap.Core/Llm/LlmConfig.cs` — `record LlmConfig(bool Enabled, string Endpoint,
  string Model, double Temperature, int TimeoutSeconds, double MinConfidence,
  string ReportTemplateDir, int ReportMaxTemplateChars)` with `LoadDefault()` reading
  `llm_config.json` from `AppContext.BaseDirectory` (pattern of `AliasRules.LoadDefault`), plus
  env overrides (`OLLAMA_HOST`, `CELMAP_LLM_MODEL`, `CELMAP_LLM_ENABLED`). Defaults:
  `http://localhost:11434`, **`gemma4:e4b` (Q4_K_M)**, temp `0`, 30s, min-conf `0.8`,
  `report_templates`, ~12000, **disabled by default**. See "Recommended model".
- New `src/CelMap.Core/Llm/LlmAssistedColumnMatcher.cs` — decorator + few-shot prompt builder
  (ADP→Workday-style column-name examples; use only provided sources, emit `null` when none fit).
- New `src/CelMap.Core/llm_config.json` — shipped default config.
- Modify `src/CelMap.Core/MappingResult.cs` — add `Llm` to `MatchKind`
  (`Fuzzy=0, Alias=1, Exact=2, Qualified=3, Llm=4`); cosmetic (decorator-only) for badging.
- Modify `src/CelMap.Core/IColumnMatcher.cs` — add `bool LlmAssistEnabled = false` to
  `MatcherOptions`; add optional `Func<HeaderColumn, IReadOnlyList<string>>? sourceSamples =
  null` to `Match` (keeps `ColumnMatcher` and all callers compiling; it ignores it).
- Modify `src/CelMap.Core/CelMap.Core.csproj` — copy `llm_config.json` to output alongside
  `synonyms.json`.

### Recommended model (as of 2026-06)
On a no-GPU CPU box, favor a strong instruction-follower with reliable structured output. The
model is fully configurable (`llm_config.json` / `CELMAP_LLM_MODEL`), so any of these swaps in
with **no code change** — just `ollama pull` it and set the name.
- **Default (CPU): `gemma4:e4b` @ `Q4_K_M`** — effective-4B, small CPU footprint, **native JSON
  structured output + function calling** (helps the Part A mapping JSON). `gemma4:e2b` for
  very low-RAM machines. Verify Gemma 4 license terms.
- **Alternative: `phi4-mini` (3.8B) @ `Q4_K_M`** — ~2.3 GB, ~12 tok/s CPU-only, MIT, 128K ctx.
- **Quality tier for the report prose (Part B): `gemma4:12b` (if ~16 GB RAM) or `qwen3:8b`
  (~5 GB, Apache-2.0)** — noticeably better, more thorough analyst report; fine for the one
  report call per run.
- **Quantization:** `Q4_K_M` (≈92% quality at ≈70% smaller). One-time `ollama pull`, offline after.

---

## Part B — Analyst data-quality report (LLM-authored, template-steered)

### B1. Profiling engine (deterministic, Core, offline)
Add `src/CelMap.Core/Quality/SourceQualityProfiler.cs` producing a
`SourceQualityReport(IReadOnlyList<ColumnQuality>)`, where
`ColumnQuality(HeaderColumn Column, bool WasMapped, int TotalRows, int BlankCount,
double BlankPct, int DistinctCount, int DuplicateRowCount,
IReadOnlyList<(string Value,int Count)> TopDuplicates, IReadOnlyList<Issue> Issues,
IReadOnlyList<string> SampleValues)` and `Issue(IssueType Type, string Detail,
IReadOnlyList<string> Evidence)`, `enum IssueType { MissingData, Duplication, Inconsistency }`.

Profiled over **every source column** (`WasMapped` set from `MappingResult.ToColumnMap()` keys
but not filtered), reading only data rows below the header. Reuse/extend `SheetDataExtensions`
(`ColumnIsEmpty`/`PopulatedRowSpan`) and `CellValue.Type`. Checks: **missing data**
(blank count/% + example rows), **duplication** (distinct vs total + top repeats), and
**inconsistencies** (mixed `CellValueType`, numbers/dates stored as text, mixed date formats,
casing/whitespace, mixed units/currency via regex; evidence = offending samples). Columns with
no issues are omitted. Entry point: `SourceQualityProfiler.Profile(source, sourceHeaders,
appliedColumnMap, srcHeaderRow)`.

### B2. Report templates (developer-steerable inputs)
Add `src/CelMap.Core/Quality/ReportTemplates.cs` that loads, from a configurable folder
(`LlmConfig.ReportTemplateDir`, default `report_templates/` resolved against
`AppContext.BaseDirectory`):
- **Structural template** — `report_structure.md`: the section skeleton the report should
  follow (e.g. *Overview*, *Per-column findings*, *Cross-cutting issues*, *Suggested questions
  for the client*, *Recommended next steps*).
- **Example reports** — every `*.md`/`*.txt` under `report_templates/examples/`: past
  human-written reports used as few-shot exemplars. Concatenated up to
  `LlmConfig.ReportMaxTemplateChars` (token-budget cap for the small CPU model; truncate with a
  marker, prefer whole files).
`ReportTemplates.LoadDefault()` mirrors `AliasRules.LoadDefault()`. Ship sensible defaults
(`report_structure.md` + one `examples/example-1.md`) so it works out of the box; the developer
edits/adds files to steer style and the kinds of questions.

### B3. Report generator (LLM prose + deterministic fallback)
Add `src/CelMap.Core/Quality/AnalystReportGenerator.cs` →
`Task<string> GenerateMarkdownAsync(SourceQualityReport report, ReportTemplates templates,
ReportContext ctx, CancellationToken ct)` where `ReportContext` carries source file/sheet name,
date, and mapping summary (counts of auto/review/unmatched, including any `Llm` matches).
- **Prompt:** system role = "data-quality analyst assistant; write a comprehensive, accurate,
  human-readable report **for an internal analyst**; follow the structure; match the style of
  the examples; include concrete **suggested questions to ask the client**; use ONLY the
  provided findings — never invent numbers." User content = the structural template + the
  example reports + the serialized deterministic findings (per-column stats, issues, evidence).
  Call `IOllamaClient.ChatTextAsync` (free-form Markdown, temp from config 0–0.4).
- **Fallback:** when `LlmConfig.Enabled` is false or the call fails/returns empty, emit a
  deterministic Markdown report built directly from the findings — the structural template's
  sections filled with the facts plus generic per-`IssueType` suggested-question lines. The
  report is therefore **always produced**, with or without the model. This is where
  "maximise CPU for the communication" lands: the model only turns correct facts into thorough,
  well-written analyst prose and question suggestions.

### B4. Output: standalone Markdown file beside the mapped workbook
Add `src/CelMap.Core/Quality/ReportWriter.cs` with
`string Write(string outputDir, string targetFileName, string markdown)` that writes
`"<target name> (data quality review).md"` into the same output directory the mapped workbook
uses (reuse the naming approach in `TargetWriter.BuildOutputPath`). `TargetWriter`/`WriteRequest`
are **unchanged** — the report is a separate, additive artifact.

### B5. Surfaces
- **WPF panel:** after a match, compute the profile + generate the report (inside the existing
  `Task.Run`), and show the Markdown in a review/preview panel (read-only render or a text box
  the analyst can tweak) before saving. On Execute (or a dedicated "Save report" action), write
  the `.md` next to the mapped file.
- **CLI:** after matching/writing, always compute the profile, generate the report, and write
  the `.md`; print its path.

### Files (Part B)
- New `src/CelMap.Core/Quality/SourceQualityProfiler.cs`, `.../ColumnQuality.cs`
  (report + `Issue`/`IssueType`), `.../ReportTemplates.cs`, `.../AnalystReportGenerator.cs`,
  `.../ReportContext.cs`, `.../ReportWriter.cs`.
- New `src/CelMap.Core/report_templates/report_structure.md` and
  `src/CelMap.Core/report_templates/examples/example-1.md` (shipped defaults).
- Modify `src/CelMap.Core/SheetDataExtensions.cs` — per-column distinct/blank counts + type
  histogram over the data range.
- Modify `src/CelMap.Core/CelMap.Core.csproj` — copy `report_templates/**` to output
  (`CopyToOutputDirectory="PreserveNewest"`), alongside `llm_config.json`/`synonyms.json`.
- Modify `src/CelMap.App/MainViewModel.cs` — generate the report in `RunMatchAsync` (reuse
  cached `_sourceData`, `_sourceHeaders`, `result.ToColumnMap()`), expose it as an
  `[ObservableProperty] string ReportMarkdown`, write it via `ReportWriter` in `WriteAsync`.
- New WPF view/section + binding for the report-review panel.

---

## Shared wiring
- `src/CelMap.App/MainViewModel.cs`: construct `LlmAssistedColumnMatcher` in the ctor
  (line ~49); add `[ObservableProperty] bool _llmAssistEnabled` near `_fuzzyEnabled`
  (~line 128); thread `LlmAssistEnabled` + `sourceSamples` (reuse `_sourceSamples`,
  line 281/35) into the two `MatcherOptions`/`Match` calls (~lines 266, 401). LLM work (mapping
  assist + report) runs inside the existing `Task.Run`, so async calls may be awaited/blocked
  off the UI thread without freezing it.
- `src/CelMap.Cli/Program.cs`: add `--llm` (and `--llm-model <name>`) flags; load `LlmConfig` +
  `ReportTemplates`; build a `sourceSamples` closure; after the existing write, generate and
  save the Markdown report and print its path.
- `src/CelMap.App` XAML: add a "Use local AI assist (Ollama)" checkbox bound to
  `LlmAssistEnabled`; teach the mapping grid badge to recognize `MatchKind.Llm`; add the
  report-review panel.
- `README.md`: document both features, the strict local-only behavior, the `--llm` flag/app
  toggle, the `llm_config.json` keys + env overrides, and **how to steer the report** by
  editing `report_templates/report_structure.md` and adding example reports under
  `report_templates/examples/`.

`IColumnMatcher.Match` stays synchronous (both consumers already call it inside `Task.Run`).

---

## Verification

1. **Unit tests** (`dotnet test tests/CelMap.Core.Tests`, no live server):
   - *Profiler:* blanks → correct count/% + evidence; repeats → duplicate counts + top
     duplicates; mixed `CellValueType`/date formats → inconsistency issues; mapped columns with
     issues still appear (scope = all columns); clean column → omitted.
   - *Templates:* structural template + example files load from the configured folder;
     concatenation respects `ReportMaxTemplateChars`; missing folder → empty templates (no throw).
   - *Report generator (fake `IOllamaClient`):* with a stub returning Markdown → that text is
     returned and contains the findings passed in; with LLM disabled/throwing/empty →
     deterministic fallback report is produced, is non-empty, contains every issue and a
     "Suggested questions" section; the prompt includes the structural template + example text.
   - *Mapping assist (fake `IOllamaClient`):* fills `Unmatched`→`Auto`+`MatchKind.Llm`; never
     reuses a claimed source; two suggestions on one source → one wins; failure → identical to
     deterministic-only; disabled → client never called.
   - *ReportWriter:* writes `"<name> (data quality review).md"` to the output dir with the given
     content.
2. **Existing suite stays green** — optional `Match` param + new enum value don't regress
   `ColumnMatcherTests`/`MatchWriteIntegrationTests`/`TargetWriterTests` (writer is untouched).
3. **Manual end-to-end (no-GPU machine):**
   - Offline, no Ollama: run CLI/App on a sheet with blanks, dupes, mixed formats → a Markdown
     report is written beside the mapped file with all findings + suggested questions (template
     fallback); no crash; `--llm` no-ops with a warning.
   - With Ollama (`ollama pull gemma4:e4b && ollama serve`, or `gemma4:12b`/`qwen3:8b` for a
     richer report): rerun → the report is well-written and follows the structural template /
     example style; edit `report_templates/` and confirm the output style/questions change
     accordingly; stop `ollama serve` → still produces the fallback report.
   - WPF: match with AI assist on → AI rows badged; report-review panel shows the Markdown for
     the analyst to read/tweak; saving writes the `.md` next to the mapped workbook.
