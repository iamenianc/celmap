# Excel Column Mapper – Product Requirements Document

**Version:** 1.0  
**Date:** June 2026  
**Status:** Ready for Development

**Tech Stack:**
- ClosedXML (read **and** write `.xlsx` while preserving target formatting)
- FuzzySharp (column matching)
- CommunityToolkit.Mvvm (WPF/MVVM)
- All open source, MIT/Apache licensed

> **Note on Excel library choice:** the original draft listed `ExcelDataReader`,
> which is **read-only** and cannot satisfy the Output requirements (writing
> values into a copy of the target while preserving its formatting). ClosedXML
> (or EPPlus, but EPPlus is non-commercial-only under its current license)
> handles both read and write. `.xls` (legacy binary) is not supported by
> ClosedXML — see §2.1 for the supported-format decision.

---

## 1. Overview

A desktop utility that maps columns between two arbitrary Excel files. User selects source and target files, identifies header rows, tool matches columns and handles type conversion, outputs mapped data.

---

## 2. Core Features

### 2.1 File & Sheet Selection
- User specifies source Excel file
- User specifies target/template Excel file (never modified; always work on a copy)
- Optionally preload a default template file from settings
- Dropdown to select sheet from each file
- Supported format: `.xlsx` (and `.xlsm`, read as data). Legacy `.xls` is out of
  scope for v1 — ClosedXML does not read it. If `.xls` support is later required,
  add a conversion step or a separate reader; do not block v1 on it.

### 2.2 Header Row Identification
- User identifies which row contains column headers in source and target
- Manual selection approach (user has final say on what's a header)

### 2.3 Column Mapping
- Match source columns to target columns using fuzzy name matching
- Surface confidence/quality of matches to user (e.g. a 0–100 score per match)
- Apply matches automatically above a configurable confidence threshold; leave
  low-confidence matches unset and flagged for manual review
- Allow user to override or adjust any mapping
- Handle ambiguous cases gracefully (when two source columns score similarly
  against one target, present both rather than silently picking one)
- Allow user to hide/show columns during preview (to reduce noise from junk/unwanted columns)
- Save and load mapping profiles by column pairs (source column name → target column name), independent of filenames
- If the same column names are present in a new session, load the last profile automatically or offer it as a quick option
- Map by column name, not position (column order in files is irrelevant)
- Warn if a mapped source column contains no data (all empty/null)

### 2.4 Type Handling
- Do not perform any type or format conversion. Values are written exactly as they exist in the source — if the source cell is text, write text; if it is a number, write the number; if it is a date serial, write the date serial.
- Type conversion is out of scope for v1 and is handled by a separate downstream process.

### 2.5 Output
- Make mapped data available (clipboard, file, or integration point)
- Include summary of what was processed and any issues encountered
- Two write modes, selectable per run via a visible UI toggle:
  - **Overwrite** (default): write mapped data starting at the row immediately
    below the target header row (typically row 2), replacing existing data rows.
    Always show a confirmation warning before proceeding.
  - **Append**: write starting at the first empty row after the last row that
    contains data (last used row), leaving existing data in place
- Output is always written to a **copy** of the target file, never the original
  (per §2.1). The copy is saved to the configured output directory. Default:
  `%USERPROFILE%\Documents\CelMap` (created on first run if missing), configurable
  in settings. (Avoid `C:\temp` as a default — it is not guaranteed to exist and
  is not a conventional user-writable location.)
- Paste values only (no formulas, formatting, or styles from source)
- Preserve all existing formatting in target file (cells, columns, rows)

---

## 3. Technical Approach

- Desktop application (Windows)
- .NET C# stack
- Excel reading: open source library
- Column matching: fuzzy string matching library
- Data types: smart conversion with fallback to original values

---

## 4. Data Flow

- Load source file
- Load target file
- Identify headers in each
- Match source columns to target columns
- Copy source values verbatim into mapped target columns
- Report results to user
- Output mapped data

---

## 5. User Experience Enhancements

Consider:
- Remembering user settings between sessions
- Saving and reusing column mapping profiles
- Smart defaults based on data inspection
- Reducing manual review overhead where confidence is high
- Keyboard shortcuts for power users
- Configurable default template/target file (preload it on startup, always work on copy)

---

## 6. Processing

Process all rows. Never block on error. Report what succeeded, what failed, why. Load and write data in arrays/bulk operations, not row-by-row iteration. Auto-detect number of rows to transfer: include all used (non-empty) rows in the source file below its header row. Hard limit: 100,000 data rows. If the source exceeds the limit, **stop before processing and warn the user** (do not silently truncate) so they can split the file or confirm a partial run.

---

## 8. Reporting

Show user a summary of what was mapped and written. Include any rows skipped or cells that could not be written. Let user decide if output is acceptable.

---

## 9. Architecture Considerations

- Separate core logic (class library / DLL) from UI (WPF EXE) so the mapping +
  conversion engine can be referenced by other apps or automated/headless callers.
  Target the core library at **current .NET (8/9)**. Legacy .NET Framework
  consumers are not a requirement.
- Config externalized (settings file) for user customization
- No heavy dependencies; use open source only

---

## 10. Non-Functional Requirements

- Handle typical Excel files (10K–100K rows, 10–200 columns)
- Performance: process data within reasonable time (seconds, not minutes)
- Stability: no crashes on valid input
- Usability: minimal learning curve for typical business user

---

## 11. Success

- Maps columns correctly across arbitrary Excel files
- Copies values verbatim without unintended conversion
- Reports issues clearly so user knows what was mapped and what was skipped
- Fast enough for typical workflows
- Easy to use (no special training needed)

