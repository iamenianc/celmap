# Plan: Local-LLM mapping assist + client-facing data-quality query document

## Context

CelMap is a .NET 10 desktop/CLI tool that maps columns between two Excel files using a
deterministic, tiered engine (Qualified > Exact > Alias > Fuzzy) and copies cell values
verbatim. Beyond mapping, the real operational need is **client communication**: when a
client's source spreadsheet has data-quality problems (missing values, duplication, mixed
formats), a human currently has to spot them and hand-write a query list to send back to the
client. This is slow and inconsistent.

This work delivers, **fully local / offline**:

- **A. Local-LLM mapping assist** — an opt-in fallback that, only for low-confidence target
  columns, asks a locally-run quantized model (Ollama) to propose a source column.
  Deterministic results stay authoritative.
- **B. Client data-quality query document (primary deliverable)** — deterministically detect
  data-quality issues across **all source columns**, then use the local model to author a
  clear, professional, **numbered list of questions** to the client. Render it as polished
  worksheet(s) (a summary/cover sheet + numbered questions, each with an evidence table) added
  to the mapped output workbook. The reviewer checks/edits it, then sends it to the client.
  The LLM is used to *maximise the quality of the written communication*; all numbers and
  evidence tables are computed deterministically so they're always correct.

**Hard constraint (per user):** nothing connects to any external/cloud server. The *only*
network activity permitted is the **one-time model download** (`ollama pull …`). At runtime
everything talks at most to a local Ollama instance on `localhost`; with Ollama absent, the
question wording falls back to deterministic templates and everything else still works.

Decisions confirmed with the user: queries cover **all source columns with issues**; the
document is written as **sheet(s) in the mapped output file**; it contains a **summary/cover
sheet** and **numbered questions each with an evidence table** (no client-response column, no
severity tags); the **LLM mapping assist (Part A) is kept**.

---

## Part A — Local-LLM mapping assist (fallback for low-confidence targets)

A decorator `LlmAssistedColumnMatcher : IColumnMatcher` wraps `ColumnMatcher`:

1. Run the deterministic matcher first.
2. If `options.LlmAssistEnabled` is false (or Ollama unreachable / nothing unresolved),
   return the deterministic result unchanged.
3. Collect targets whose status is not `Auto`, and source columns **not** already claimed by
   a deterministic `Auto` match.
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
  text variant is reused by Part B for prose).
- New `src/CelMap.Core/Llm/OllamaClient.cs` — `HttpClient` to a configurable `localhost`
  endpoint, **built-in** `System.Net.Http` + `System.Text.Json` (no new NuGet dep).
- New `src/CelMap.Core/Llm/LlmConfig.cs` — `record LlmConfig(bool Enabled, string Endpoint,
  string Model, double Temperature, int TimeoutSeconds, double MinConfidence)` with
  `LoadDefault()` reading `llm_config.json` from `AppContext.BaseDirectory` (same pattern as
  `AliasRules.LoadDefault`), plus env-var overrides (`OLLAMA_HOST`, `CELMAP_LLM_MODEL`,
  `CELMAP_LLM_ENABLED`). Defaults: `http://localhost:11434`, **`phi4-mini` (3.8B, Q4_K_M)**,
  temp `0`, 30s, min-confidence `0.8`, **disabled by default**. See "Recommended model".
- New `src/CelMap.Core/Llm/LlmAssistedColumnMatcher.cs` — decorator + few-shot prompt builder.
  System prompt: task + output contract + worked column-name examples (adapted from the
  ADP→Workday example, e.g. `Emp_ID`→`employee_id`); use only provided sources, emit `null`
  when none fit.
- New `src/CelMap.Core/llm_config.json` — shipped default config.
- Modify `src/CelMap.Core/MappingResult.cs` — add `Llm` to `MatchKind`
  (`Fuzzy=0, Alias=1, Exact=2, Qualified=3, Llm=4`); cosmetic (decorator-only) so the UI/CLI
  can badge AI matches.
- Modify `src/CelMap.Core/IColumnMatcher.cs` — add `bool LlmAssistEnabled = false` to
  `MatcherOptions`; add optional `Func<HeaderColumn, IReadOnlyList<string>>? sourceSamples =
  null` to `Match` (keeps `ColumnMatcher` and all callers compiling; it ignores it).
- Modify `src/CelMap.Core/CelMap.Core.csproj` — add `Content Include="llm_config.json"
  CopyToOutputDirectory="PreserveNewest"` alongside the existing `synonyms.json` entry.

### Recommended model (as of 2026-06)
On a no-GPU CPU box, favor a strong instruction-follower with reliable structured output.
The model is fully configurable (`llm_config.json` / `CELMAP_LLM_MODEL`), so any of these can
be swapped in with **no code change** — just `ollama pull` it and set the name.
- **Default (CPU): `gemma4:e4b` @ `Q4_K_M`** — effective-4B, small CPU footprint, and
  **native JSON structured output + function calling**, which makes the Part A mapping JSON
  more reliable. (`gemma4:e2b` for very low-RAM machines.) Verify Gemma 4 license terms.
- **Alternative default: `phi4-mini` (3.8B) @ `Q4_K_M`** — ~2.3 GB, ~12 tok/s CPU-only, MIT,
  128K ctx; solid for the small mapping JSON (JSON locked via Ollama `format`).
- **Quality tier for client prose (Part B): `gemma4:12b` (if ~16 GB RAM) or `qwen3:8b`
  (~5 GB, Apache-2.0)** — noticeably better wording for the client-facing questions; fine for
  the one batched call per run.
- **Quantization:** `Q4_K_M` (≈92% quality at ≈70% smaller). One-time `ollama pull`, then
  fully offline.

---

## Part B — Client data-quality query document (primary deliverable)

### B1. Profiling engine (deterministic, Core, offline)
Add `src/CelMap.Core/Quality/SourceQualityProfiler.cs` producing a
`SourceQualityReport(IReadOnlyList<ColumnQuality>)`, where
`ColumnQuality(HeaderColumn Column, bool WasMapped, int TotalRows, int BlankCount,
double BlankPct, int DistinctCount, int DuplicateRowCount,
IReadOnlyList<(string Value,int Count)> TopDuplicates,
IReadOnlyList<Issue> Issues, IReadOnlyList<string> SampleValues)` and
`Issue(IssueType Type, string Detail, IReadOnlyList<string> Evidence)` with
`enum IssueType { MissingData, Duplication, Inconsistency }`.

Profiled over **every source column** (set `WasMapped` from `MappingResult.ToColumnMap()`
keys, but do **not** filter — all columns with issues are reported), reading only the data
rows below the header. Reuse/extend `SheetDataExtensions` (`ColumnIsEmpty`/`PopulatedRowSpan`)
and `CellValue.Type` (`Empty/Text/Number/Boolean/DateTime/Error`). "Easy to compute" checks:
- **Missing data:** blank/`Empty` count and percentage (evidence: example blank row numbers).
- **Duplication:** distinct vs. total; top repeated values with counts (evidence: the repeated
  values + counts).
- **Inconsistencies (cheap heuristics):** mixed `CellValueType` in a column (Number + Text),
  numbers/dates stored as text, mixed date formats, inconsistent casing / stray whitespace,
  mixed units/currency symbols via simple regex (evidence: a few offending sample values).

Entry point: `SourceQualityProfiler.Profile(SheetData source, IReadOnlyList<HeaderColumn>
sourceHeaders, IReadOnlyDictionary<int,int> appliedColumnMap, int srcHeaderRow)`. Columns with
no issues are omitted from the report.

### B2. Query document generator (deterministic layout + LLM-authored prose)
Add `src/CelMap.Core/Quality/QualityQueryGenerator.cs` producing a `ClientQueryDocument`:
- `DocumentSummary(int QueryCount, int ColumnCount, DateOnly GeneratedOn,
  IReadOnlyList<QueryIndexEntry> Index)`
- `Query(int Number, string ColumnLabel, IssueType Type, string QuestionText,
  EvidenceTable Evidence)`; `EvidenceTable(IReadOnlyList<string> Headers,
  IReadOnlyList<IReadOnlyList<string>> Rows)`.

One numbered `Query` **per issue** (in column order, issues grouped per column). Numbers and
evidence tables are built **deterministically** from `SourceQualityReport`. The **question
wording** is authored by the local model:
- Single batched `ChatJsonAsync` call: send the structured list of issues (column, type,
  detail, key stats) and ask for a JSON array of `{number, question}` — professional,
  client-appropriate phrasing, one per issue. `temperature` from config (0–0.3).
- **Fallback:** when `LlmConfig.Enabled` is false or the call fails/returns junk, fill each
  `QuestionText` from a deterministic per-`IssueType` template (e.g. "Column '{label}' has
  {n} blank entries ({pct}%). Please confirm whether these should be populated, and supply
  the missing values."). The document is therefore always produced, with or without the model.

This is where "maximise CPU power for the communication" lands: the model's whole job is to
turn correct findings into clear client questions; it never invents numbers.

### B3. Rendering: worksheets in the mapped output file
Extend the writer to add the query document as worksheet(s) before `wb.Save()`
(`src/CelMap.Core/TargetWriter.cs:154`, where the `XLWorkbook wb` is already open). Add an
optional `ClientQueryDocument? QueryDocument` to `WriteRequest`; when present:
- **"Data Quality Summary"** sheet — title, generated date, a `[Client name]` placeholder
  cell, totals ("{QueryCount} queries across {ColumnCount} columns"), and an index table
  (Q#, Column, Issue type, short question). Styled with bold headers + fill (ClosedXML
  `Style.Font.Bold` / `Style.Fill`, as already used in `TargetWriterTests`).
- **"Data Quality Queries"** sheet — each numbered question as a bold heading + wrapped
  question text, followed by its evidence table (bordered, header-filled), with spacing
  between questions and auto-fit columns.

No new file is created and the existing data-writing path is untouched (the report sheets are
purely additive).

### B4. Surfaces
- **WPF panel:** after a match, compute the report (deterministic) and generate the document;
  show a review panel listing the numbered questions + evidence so the human can read them
  before Execute. (Stretch: allow editing question text inline before write.) On Execute, pass
  the `ClientQueryDocument` into `WriteRequest` so it lands in the output workbook.
- **Output sheets:** as in B3, for both App and CLI runs.

### Files (Part B)
- New `src/CelMap.Core/Quality/SourceQualityProfiler.cs`, `.../ColumnQuality.cs` (report +
  `Issue`/`IssueType`), `.../QualityQueryGenerator.cs`, `.../ClientQueryDocument.cs`.
- Modify `src/CelMap.Core/SheetDataExtensions.cs` — small profiling helpers (per-column
  distinct/blank counts, type histogram over the data range).
- Modify `src/CelMap.Core/TargetWriter.cs` + `WriteRequest` — optional `QueryDocument`;
  render the two sheets before `wb.Save()`.
- Modify `src/CelMap.App/MainViewModel.cs` — compute report + generate document in
  `RunMatchAsync` (reuse cached `_sourceData`, `_sourceHeaders`, `result.ToColumnMap()`),
  expose it as `[ObservableProperty]`, include it in the `WriteRequest` built in `WriteAsync`.
- New WPF view/section + binding for the query-review panel (follow existing Material Design
  patterns in the mapping view).

---

## Shared wiring
- `src/CelMap.App/MainViewModel.cs`: construct `LlmAssistedColumnMatcher` in the ctor
  (line ~49); add `[ObservableProperty] bool _llmAssistEnabled` near `_fuzzyEnabled`
  (~line 128); thread `LlmAssistEnabled` + `sourceSamples` (reuse `_sourceSamples`,
  line 281/35) into the two `MatcherOptions`/`Match` calls (~lines 266, 401). LLM work runs
  inside the existing `Task.Run`, so the decorator/generator may block on the async client
  (`.GetAwaiter().GetResult()`) without freezing the UI.
- `src/CelMap.Cli/Program.cs`: add `--llm` (and `--llm-model <name>`) flags; load `LlmConfig`;
  build a `sourceSamples` closure from `sourceData`; always compute the report, generate the
  query document, and write it into the output workbook.
- `src/CelMap.App` XAML: add a "Use local AI assist (Ollama)" checkbox bound to
  `LlmAssistEnabled`; teach the mapping grid badge to recognize `MatchKind.Llm`; add the
  data-quality query-review panel.
- `README.md`: document both features, the strict local-only behavior (Ollama on localhost;
  one-time `ollama pull phi4-mini` / `qwen3:8b`; offline thereafter), the `--llm` flag/app
  toggle, and the `llm_config.json` keys + env-var overrides.

`IColumnMatcher.Match` stays synchronous (both consumers already call it inside `Task.Run`),
avoiding cascading async signature changes.

---

## Verification

1. **Unit tests** (`dotnet test tests/CelMap.Core.Tests`, no live server):
   - *Profiler:* blanks → correct blank count/% + evidence; repeated values → duplicate counts
     + top-duplicates; mixed `CellValueType`/date formats → inconsistency issues; **mapped**
     columns with issues still appear (scope = all columns); clean column → omitted.
   - *Query generator (fake `IOllamaClient`):* N issues → N numbered queries with correct
     evidence tables; LLM returns wording → applied; LLM disabled/throws/invalid JSON →
     deterministic template wording, document still complete; numbering is stable and 1-based.
   - *Mapping assist (fake `IOllamaClient`):* suggestion fills an `Unmatched` target → `Auto` +
     `MatchKind.Llm`; deterministically-claimed source never reassigned; two suggestions on one
     source → one wins; failure → identical to deterministic-only; disabled → client not called.
   - *Writer:* `WriteRequest` with a `QueryDocument` → output workbook gains "Data Quality
     Summary" + "Data Quality Queries" sheets with expected rows/tables and bold styling;
     without it → unchanged behavior (existing `TargetWriterTests` stay green).
2. **Existing suite stays green** — optional `Match` param, new enum value, and `WriteRequest`
   change don't regress `ColumnMatcherTests`, `MatchWriteIntegrationTests`, `TargetWriterTests`.
3. **Manual end-to-end (no-GPU machine):**
   - Offline, no Ollama: run CLI/App on a sheet with blanks, dupes, and mixed formats → output
     workbook contains the two query sheets with correct numbers/evidence and template wording;
     no crash; `--llm` no-ops with a warning.
   - With Ollama (`ollama pull qwen3:8b && ollama serve`): rerun → questions are well-worded
     client prose, numbers/evidence identical to the deterministic run; stop `ollama serve` and
     rerun → still produces the document via templates.
   - WPF: match with AI assist on → AI-mapped rows badged; query-review panel lists numbered
     questions; after Execute the mapped file contains the summary + queries sheets ready for a
     human to review and send to the client.
