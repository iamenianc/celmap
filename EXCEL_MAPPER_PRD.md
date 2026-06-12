# Excel Column Mapper – Product Requirements Document

**Version:** 1.0  
**Date:** June 2026  
**Status:** Ready for Development

**Tech Stack:**
- ExcelDataReader (read Excel files)
- Raffinert.FuzzySharp (column matching)
- CommunityToolkit.Mvvm (WPF/MVVM)
- All open source, MIT/Apache licensed

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
- Support for common Excel formats

### 2.2 Header Row Identification
- User identifies which row contains column headers in source and target
- Manual selection approach (user has final say on what's a header)

### 2.3 Column Mapping
- Match source columns to target columns
- Surface confidence/quality of matches to user
- Allow user to override or adjust any mapping
- Handle ambiguous cases gracefully
- Allow user to hide/show columns during preview (to reduce noise from junk/unwanted columns)
- Save and load mapping profiles by file pair (source + target files remembered)
- If same file pair is used again, load last profile automatically or offer as quick option
- Map by column name, not position (column order in files is irrelevant)
- Warn if mapping a source column that contains no data (all empty/null)

### 2.4 Type Handling
- Detect intended data types in target columns
- Attempt to convert source data to target types
- Surface conversion issues without blocking processing
- Provide user visibility into what was converted and what failed

### 2.5 Output
- Make mapped data available (clipboard, file, or integration point)
- Include summary of what was processed and any issues encountered
- Default: write mapped data to target file starting at row 2 (preserving any header)
- Option: append to end of existing data in target file (find last used row)
- When writing to file, save copy to configured output directory (default: C:/temp, configurable in settings)
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
- Detect target column data types
- Convert source values to target types
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

Process all rows. Never block on error. Report what succeeded, what failed, why. Load and write data in arrays/bulk operations, not row-by-row iteration. Auto-detect number of rows to transfer: include all used rows in source file. Hard limit: 100,000 rows maximum.

---

## 7. Type Conversion

Attempt conversion to target type. On failure, retain original value and log issue. Handle at minimum: numbers (with format variants), dates (with format variants), booleans (with value mapping).

---

## 8. Reporting

Show user a summary and detailed breakdown of conversions. Include what succeeded, what failed, why. Let user decide if output is acceptable.

---

## 9. Architecture Considerations

- Separate core logic (DLL) from UI (EXE) to allow reuse by legacy apps
- Config externalized for user customization
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
- Handles type mismatches without crashing
- Reports issues clearly so user knows what was converted and what wasn't
- Fast enough for typical workflows
- Easy to use (no special training needed)

---

## 12. Dependencies

Open source libraries only. No licensing costs.

---

## 13. Timeline

Roughly 4–5 weeks for core functionality + enhancements. Adjust based on actual implementation complexity.
